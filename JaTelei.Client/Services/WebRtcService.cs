using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Media.Imaging;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using JaTelei.Client.Models;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace JaTelei.Client.Services;

// =============================================================================
// WebRtcService — Media Engine v2
//
//  SENDER PIPELINE (GPU zero-copy, C++ DLL + Opus):
//    WGC / DXGI DDup → D3D11VP (BGRA→NV12) → NVENC/AMF/QSV → H.264/AV1
//      → JitterBuffer → RTP SendVideo()
//    NAudio WasapiLoopback → AudioEngine (Concentus Opus) → RTP SendAudio()
//
//  RECEIVER PIPELINE:
//    RTP H.264 → JitterBuffer → MfH264Decoder → FrameReceived (BGRA)
//    RTP Opus  → AudioEngine (Concentus decode) → WaveOutPlayer (48kHz stereo)
//
//  AV SYNC:
//    AVSyncManager expects PTS in 100-ns ticks (10 000 ticks = 1 ms).
//    Both video and audio PTS are normalised to start at 0 (first-seen
//    RTP timestamp subtracted) to avoid the large random initial offset
//    that RTP timestamps carry.
//
//  PUBLIC INTERFACE: IDENTICAL TO v1 — no callers need to change.
// =============================================================================

public class WebRtcService : IAsyncDisposable
{
    // ── Core SIPSorcery state ─────────────────────────────────────────────────

    private RTCPeerConnection?          _pc;
    private CancellationTokenSource?    _cts;
    private MfH264Decoder?              _decoder;

    // ICE candidates queued before _pc is created
    private readonly List<RTCIceCandidateInit> _pendingCandidates = new();
    private readonly object                    _candidateLock     = new();

    // ── Media Engine components ───────────────────────────────────────────────

    private AudioEngine?         _audioEngine;
    private AdaptiveController?  _adaptive;
    private readonly AVSyncManager    _avSync      = new();
    private readonly JitterBuffer     _jitterBuf   = new();

    // ── Diagnostics & state flags ─────────────────────────────────────────────

    private int  _framesRecv;
    private int  _framesSent;
    private volatile bool _forceGdiKeyframe;
    private volatile bool _requestDllReinit;  // triggers DLL Shutdown+Init → forces IDR
    private int  _previewCount;

    // Normalised RTP timestamp anchors (receiver side).
    // RTP timestamps start at a random offset; we subtract the first-seen
    // value so both audio and video PTS begin at 0 and AVSyncManager sees
    // real relative drift rather than a huge constant offset.
    private uint _firstVideoRtpTs = uint.MaxValue;
    private uint _firstAudioRtpTs = uint.MaxValue;

    internal static readonly string LogPath =
        Path.Combine(Path.GetTempPath(), "jatelei_error.txt");

    private static void Log(string msg) =>
        File.AppendAllText(LogPath, $"[WebRTC] {DateTime.Now:HH:mm:ss.fff} {msg}\n");

    // ── ICE / RTC config ──────────────────────────────────────────────────────

    private static RTCConfiguration BuildRtcConfig()
    {
        var cfg = App.Config;
        var servers = new List<RTCIceServer>
        {
            new() { urls = "stun:stun.l.google.com:19302" },
            new() { urls = "stun:stun1.l.google.com:19302" },
        };

        var turnUrl  = cfg["Ice:TurnUrl"];
        var turnUser = cfg["Ice:TurnUsername"];
        var turnCred = cfg["Ice:TurnCredential"];

        if (!string.IsNullOrWhiteSpace(turnUrl) && !string.IsNullOrWhiteSpace(turnUser))
        {
            servers.Add(new() { urls = turnUrl,                    username = turnUser, credential = turnCred });
            servers.Add(new() { urls = $"{turnUrl}?transport=tcp", username = turnUser, credential = turnCred });
        }

        return new RTCConfiguration { iceServers = servers };
    }

    private static readonly Lazy<RTCConfiguration> _lazyRtcConfig = new(BuildRtcConfig);
    private static RTCConfiguration RtcConfig => _lazyRtcConfig.Value;

    // ── Public events & properties ────────────────────────────────────────────

    public event Action<byte[], int, int>? FrameReceived;
    public event Action<string>?           IceCandidateReady;
    public event Action<string>?           IceStateChanged;
    public event Action<BitmapSource>?     SenderPreviewFrame;

    public bool IsConnected =>
        _pc?.iceConnectionState == RTCIceConnectionState.connected;

    // ── GDI P/Invoke (fallback when DLL fails) ────────────────────────────────

    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
    private const uint PW_RENDERFULLCONTENT = 0x00000002;

    // =========================================================================
    // SENDER — CreateOffer + StartCapture
    // =========================================================================

    public async Task<string> CreateOfferAsync()
    {
        _pc = new RTCPeerConnection(RtcConfig);

        var videoTrack = new MediaStreamTrack(
            new List<VideoFormat> { new VideoFormat(VideoCodecsEnum.H264, 96) },
            MediaStreamStatusEnum.SendOnly);
        _pc.addTrack(videoTrack);

        var audioTrack = new MediaStreamTrack(
            new List<AudioFormat> { new AudioFormat(AudioCodecsEnum.OPUS, 111, 48000, 2) },
            MediaStreamStatusEnum.SendOnly);
        _pc.addTrack(audioTrack);

        WireIceEvents("Sender");
        WireOnConnected(isSender: true);

        var offer = _pc.createOffer();
        _pc.setLocalDescription(offer);
        return await Task.FromResult(offer.sdp);
    }

    public Task SetRemoteAnswerAsync(string sdp)
    {
        _pc!.setRemoteDescription(new RTCSessionDescriptionInit
        {
            type = RTCSdpType.answer,
            sdp  = sdp
        });
        return Task.CompletedTask;
    }

    // =========================================================================
    // RECEIVER — CreateAnswer
    // =========================================================================

    public Task<string> CreateAnswerAsync(string offerSdp)
    {
        _pc = new RTCPeerConnection(RtcConfig);
        _framesRecv        = 0;
        _firstVideoRtpTs   = uint.MaxValue;
        _firstAudioRtpTs   = uint.MaxValue;
        _decoder           = new MfH264Decoder();

        // ── Video receive ─────────────────────────────────────────────────────
        _pc.OnVideoFrameReceived += OnVideoFrameReceived;

        var videoTrack = new MediaStreamTrack(
            new List<VideoFormat> { new VideoFormat(VideoCodecsEnum.H264, 96) },
            MediaStreamStatusEnum.RecvOnly);
        _pc.addTrack(videoTrack);

        // ── Audio receive ─────────────────────────────────────────────────────
        var player   = new WaveOutPlayer();
        _audioEngine = new AudioEngine(sendOpus: null, player: player);

        _pc.OnRtpPacketReceived += (ep, mediaType, rtpPacket) =>
        {
            if (mediaType != SDPMediaTypesEnum.audio) return;

            // Normalise: subtract first-seen RTP timestamp so PTS starts at 0.
            // AVSyncManager expects 100-ns ticks (10 000 = 1 ms).
            // 48 kHz Opus clock: 1 tick = 1/48000 s = 20 833 ns ≈ 208.33 100ns-ticks
            uint rawTs = rtpPacket.Header.Timestamp;
            if (_firstAudioRtpTs == uint.MaxValue) _firstAudioRtpTs = rawTs;
            uint delta = rawTs - _firstAudioRtpTs; // handles 32-bit wraparound
            long audioPts = (long)delta * 10_000_000L / 48_000L;

            _avSync.ReportAudio(audioPts);
            _audioEngine.OnOpusReceived(rtpPacket.Payload);
        };

        var audioTrack = new MediaStreamTrack(
            new List<AudioFormat> { new AudioFormat(AudioCodecsEnum.OPUS, 111, 48000, 2) },
            MediaStreamStatusEnum.RecvOnly);
        _pc.addTrack(audioTrack);

        // ── AV sync handlers ─────────────────────────────────────────────────
        // OnDiscontinuity: log only — do NOT flush jitter buffer.
        // Flushing on every perceived discontinuity caused the jitter buffer to
        // be cleared dozens of times per second due to mismatched PTS units in v1.0.143.
        _avSync.OnDrift         += driftMs => Log($"AV drift: {driftMs:+0.0;-0.0}ms");
        _avSync.OnDiscontinuity += ()      => Log("AV discontinuity detected (logged only — no flush)");

        WireIceEvents("Recv");
        WireOnConnected(isSender: false);

        _pc.setRemoteDescription(new RTCSessionDescriptionInit
        {
            type = RTCSdpType.offer,
            sdp  = offerSdp
        });
        var answer = _pc.createAnswer();
        _pc.setLocalDescription(answer);

        DrainPendingCandidates();
        return Task.FromResult(answer.sdp);
    }

    // ── Video frame handler (receiver) ────────────────────────────────────────

    private void OnVideoFrameReceived(IPEndPoint ep, uint ts, byte[] frame, VideoFormat fmt)
    {
        try
        {
            var h264 = EnsureAnnexB(frame);
            _framesRecv++;

            // Normalise RTP timestamp → 100-ns ticks relative to stream start.
            // NVENC uses intra-refresh (all frames are P-type at NAL level), so
            // we decode from the very first frame and let intra-refresh heal the
            // picture over the first N frames. Waiting for an IDR (as in v1.0.143)
            // only wastes 5 seconds and then starts decoding from a worse position.
            if (_firstVideoRtpTs == uint.MaxValue) _firstVideoRtpTs = ts;
            uint   delta  = ts - _firstVideoRtpTs;
            long   pts    = (long)delta * 10_000_000L / 90_000L;
            ushort seq    = (ushort)(_framesRecv & 0xFFFF);

            // Diagnostic logging for first 10 + every 300 frames
            if (_framesRecv <= 10 || _framesRecv % 300 == 0)
            {
                var nalTypes = DetectNalTypes(h264);
                Log($"Recv frame#{_framesRecv} {h264.Length}B NALs=[{nalTypes}]");
            }

            _jitterBuf.Push(seq, h264, false, pts);

            while (_jitterBuf.TryPop(out var jf) && jf != null)
            {
                _avSync.ReportVideo(jf.Pts);
                var (bgra, w, h) = _decoder!.Decode(jf.Data);
                if (bgra != null)
                    FrameReceived?.Invoke(bgra, w, h);
            }
        }
        catch (Exception ex)
        {
            Log($"OnVideoFrameReceived: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // =========================================================================
    // ICE
    // =========================================================================

    public Task AddIceCandidateAsync(string candidateJson)
    {
        try
        {
            var init = JsonSerializer.Deserialize<RTCIceCandidateInit>(candidateJson)
                       ?? throw new ArgumentException("ICE candidate inválido");
            lock (_candidateLock)
            {
                if (_pc == null)
                {
                    _pendingCandidates.Add(init);
                    return Task.CompletedTask;
                }
            }
            _pc.addIceCandidate(init);
        }
        catch (Exception ex)
        {
            Log($"AddIceCandidate: {ex.GetType().Name}: {ex.Message}");
        }
        return Task.CompletedTask;
    }

    private void DrainPendingCandidates()
    {
        List<RTCIceCandidateInit> pending;
        lock (_candidateLock)
        {
            pending = new List<RTCIceCandidateInit>(_pendingCandidates);
            _pendingCandidates.Clear();
        }
        foreach (var c in pending)
        {
            try   { _pc!.addIceCandidate(c); }
            catch (Exception ex) { Log($"DrainCandidate: {ex.GetType().Name}: {ex.Message}"); }
        }
    }

    private void WireIceEvents(string role)
    {
        _pc!.onicecandidate += c =>
        {
            var json = c.toJSON();
            Log($"[{role}/ICE] [{ExtractCandType(json)}] {json}");
            IceCandidateReady?.Invoke(json);
        };
        _pc.oniceconnectionstatechange += state =>
        {
            Log($"[{role}/ICE] iceState={state}");
            IceStateChanged?.Invoke(state.ToString());
        };
        _pc.onconnectionstatechange += state =>
            Log($"[{role}/ICE] connState={state}");
    }

    private void WireOnConnected(bool isSender)
    {
        _pc!.oniceconnectionstatechange += state =>
        {
            if (state != RTCIceConnectionState.connected) return;

            if (isSender)
            {
                _forceGdiKeyframe = true;
                _requestDllReinit = true;
                Log("ICE connected — DLL reinit scheduled for IDR");

                _ = Task.Run(async () =>
                {
                    for (int i = 0; i < 100; i++)
                    {
                        var eng = _audioEngine;
                        if (eng != null)
                        {
                            eng.StartSender();
                            Log("Audio sender started after ICE connect (NAudio loopback)");
                            return;
                        }
                        await Task.Delay(100).ConfigureAwait(false);
                    }
                    Log("WARN: audio sender never started — AudioEngine not ready after 10s");
                });
            }
        };
    }

    // =========================================================================
    // CAPTURE LOOP (sender)
    // =========================================================================

    public void StartCapture(int fps = 30, ShareTarget? target = null)
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        int effectiveFps = target?.Fps > 0 ? target.Fps : fps;
        int bitrateKbps  = 20_000;
        int targetHeight = target?.ResolutionHeight ?? 0;

        uint rtpDuration = (uint)(90_000.0 / effectiveFps);
        var  delay       = TimeSpan.FromMilliseconds(1000.0 / effectiveFps);

        Task.Run(async () =>
        {
            bool dllReady = false;

            IntPtr hwnd = target?.WindowHandle ?? IntPtr.Zero;
            int    dstH = targetHeight > 0 ? targetHeight : 0;

            // Audio engine created unconditionally (NAudio doesn't need DLL)
            {
                const uint opusRtpDuration = 960;
                Action<byte[]> sendOpus = payload =>
                {
                    try { _pc?.SendAudio(opusRtpDuration, payload); }
                    catch { }
                };
                _audioEngine = new AudioEngine(sendOpus: sendOpus, player: null);
            }

            // ── DLL init ──────────────────────────────────────────────────────
            try
            {
                dllReady = ScreenCaptureService.Initialize(
                    dstWidth:      0,
                    dstHeight:     dstH,
                    fps:           effectiveFps,
                    bitrateKbps:   bitrateKbps,
                    windowHandle:  hwnd,
                    captureMode:   JcCaptureMode.Auto,
                    codec:         JcCodec.H264,
                    encoderVendor: JcEncoderVendor.Auto,
                    enableAudio:   true);

                Log(dllReady
                    ? $"DLL OK ({ScreenCaptureService.OutputWidth}x{ScreenCaptureService.OutputHeight})"
                    : "DLL init failed — falling back to GDI");
            }
            catch (Exception ex)
            {
                Log($"DLL init exception: {ex.GetType().Name}: {ex.Message}");
            }

            // ── Adaptive controller ───────────────────────────────────────────
            if (dllReady && _pc != null)
            {
                _adaptive = new AdaptiveController();
                _adaptive.Attach(_pc);
                _adaptive.ForceProfile((int)2); // HIGH = index 2
            }

            MfH264Encoder? gdiEncoder       = null;
            bool           gdiEncoderFailed = false;
            int            encW = 0, encH = 0;
            ushort         txSeq = 0;
            _framesSent = 0;

            try
            {
                while (!token.IsCancellationRequested)
                {
                    var t0 = DateTime.UtcNow;
                    try
                    {
                        // ── DLL reinit to force IDR ───────────────────────────
                        if (_requestDllReinit && dllReady)
                        {
                            _requestDllReinit = false;
                            Log("DLL reinit requested — restarting encoder to force IDR");
                            ScreenCaptureService.Shutdown();
                            await Task.Delay(80).ConfigureAwait(false);
                            dllReady = ScreenCaptureService.Initialize(
                                dstWidth:      0,
                                dstHeight:     dstH,
                                fps:           effectiveFps,
                                bitrateKbps:   bitrateKbps,
                                windowHandle:  hwnd,
                                captureMode:   JcCaptureMode.Auto,
                                codec:         JcCodec.H264,
                                encoderVendor: JcEncoderVendor.Auto,
                                enableAudio:   true);
                            Log(dllReady ? "DLL reinit OK — IDR frame incoming" : "DLL reinit FAILED — using GDI");
                        }

                        byte[]? h264  = null;
                        bool    isKey = false;

                        if (dllReady)
                        {
                            var result = ScreenCaptureService.CaptureFrame();
                            h264  = result.Video;
                            isKey = result.IsKeyFrame;
                        }
                        else
                        {
                            byte[]? raw = null;
                            int w = 0, h = 0;

                            if (target?.WindowHandle is { } hwndFb && hwndFb != IntPtr.Zero)
                                raw = CaptureWindow(hwndFb, out w, out h);
                            else if (target?.MonitorBounds is System.Windows.Rect bounds)
                                raw = CaptureRegion(
                                    (int)bounds.X, (int)bounds.Y,
                                    (int)bounds.Width, (int)bounds.Height, out w, out h);
                            else
                            {
                                int scrW = (int)System.Windows.SystemParameters.PrimaryScreenWidth;
                                int scrH = (int)System.Windows.SystemParameters.PrimaryScreenHeight;
                                raw = CaptureRegion(0, 0, scrW, scrH, out w, out h);
                            }

                            if (raw != null && w > 0 && h > 0)
                            {
                                int encTargetH = targetHeight > 0 ? targetHeight : h;
                                int encTargetW = (int)(w * ((double)encTargetH / h));
                                if (encTargetW % 16 != 0) encTargetW = (encTargetW / 16) * 16;
                                if (encTargetH % 16 != 0) encTargetH = (encTargetH / 16) * 16;
                                if (encTargetW <= 0) encTargetW = 16;
                                if (encTargetH <= 0) encTargetH = 16;

                                if (!gdiEncoderFailed)
                                {
                                    if (gdiEncoder == null || encTargetW != encW || encTargetH != encH)
                                    {
                                        gdiEncoder?.Dispose();
                                        gdiEncoder = null;
                                        try
                                        {
                                            gdiEncoder = new MfH264Encoder(encTargetW, encTargetH,
                                                                           effectiveFps, bitrateKbps * 1000);
                                            encW = encTargetW; encH = encTargetH;
                                        }
                                        catch (Exception ex)
                                        {
                                            gdiEncoderFailed = true;
                                            Log($"GdiEncoder init: {ex.GetType().Name}: {ex.Message}");
                                        }
                                    }
                                    if (gdiEncoder != null)
                                    {
                                        if (_forceGdiKeyframe)
                                        {
                                            _forceGdiKeyframe = false;
                                            gdiEncoder.ForceKeyframe();
                                            isKey = true;
                                        }
                                        h264 = gdiEncoder.Encode(raw, w, h);
                                    }
                                }
                            }
                        }

                        if (h264?.Length > 0)
                        {
                            long pts = (long)(DateTime.UtcNow.Ticks - 621355968000000000L) * 100L;
                            _jitterBuf.Push(txSeq++, h264, isKey, pts);
                            _avSync.ReportVideo(pts);

                            while (_jitterBuf.TryPop(out var jf) && jf != null)
                            {
                                try
                                {
                                    _pc?.SendVideo(rtpDuration, jf.Data);
                                    _framesSent++;

                                    if (_framesSent <= 15 || _framesSent % 300 == 0)
                                    {
                                        var nalTypes = DetectNalTypes(jf.Data);
                                        Log($"Sent frame#{_framesSent} {jf.Data.Length}B NALs=[{nalTypes}] key={isKey}");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Log($"SendVideo: {ex.GetType().Name}: {ex.Message}");
                                }
                            }
                        }

                        if (SenderPreviewFrame != null && (++_previewCount % 15 == 0))
                        {
                            try
                            {
                                int scrW = (int)System.Windows.SystemParameters.PrimaryScreenWidth;
                                int scrH = (int)System.Windows.SystemParameters.PrimaryScreenHeight;
                                var raw2 = CaptureRegion(0, 0, scrW, scrH, out int pw, out int ph);
                                if (raw2 != null && pw > 0 && ph > 0)
                                {
                                    var wb = new WriteableBitmap(pw, ph, 96, 96,
                                        System.Windows.Media.PixelFormats.Bgra32, null);
                                    wb.WritePixels(new System.Windows.Int32Rect(0, 0, pw, ph), raw2, pw * 4, 0);
                                    wb.Freeze();
                                    System.Windows.Application.Current?.Dispatcher.Invoke(
                                        () => SenderPreviewFrame?.Invoke(wb));
                                }
                            }
                            catch { }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"CaptureLoop: {ex.GetType().Name}: {ex.Message}");
                    }

                    var wait = delay - (DateTime.UtcNow - t0);
                    if (wait > TimeSpan.Zero)
                        await Task.Delay(wait, token).ConfigureAwait(false);
                }
            }
            finally
            {
                gdiEncoder?.Dispose();
                if (_audioEngine != null) { await _audioEngine.DisposeAsync(); _audioEngine = null; }
                _adaptive?.Dispose(); _adaptive = null;
                if (dllReady) ScreenCaptureService.Shutdown();
            }
        }, token);
    }

    public void StopCapture()
    {
        _cts?.Cancel();
        _cts = null;
    }

    // =========================================================================
    // GDI helpers (fallback)
    // =========================================================================

    private static byte[] CaptureWindow(IntPtr hwnd, out int width, out int height)
    {
        GetWindowRect(hwnd, out var rect);
        width  = rect.Right  - rect.Left;
        height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) { width = 1; height = 1; return new byte[4]; }
        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            var hdc = g.GetHdc();
            PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT);
            g.ReleaseHdc(hdc);
        }
        return BitmapToBgra(bmp, width, height);
    }

    private static byte[] CaptureRegion(int x, int y, int w, int h, out int width, out int height)
    {
        width = w; height = h;
        if (w <= 0 || h <= 0) { width = 1; height = 1; return new byte[4]; }
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(x, y, 0, 0, new Size(w, h));
        return BitmapToBgra(bmp, w, h);
    }

    private static byte[] BitmapToBgra(Bitmap bmp, int w, int h)
    {
        var bd    = bmp.LockBits(new Rectangle(0, 0, w, h),
                                 ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var bytes = new byte[Math.Abs(bd.Stride) * h];
        Marshal.Copy(bd.Scan0, bytes, 0, bytes.Length);
        bmp.UnlockBits(bd);
        return bytes;
    }

    // =========================================================================
    // Utilities
    // =========================================================================

    private static string ExtractCandType(string j)
    {
        foreach (var t in new[] { "relay", "srflx", "prflx", "host" })
            if (j.Contains($"typ {t}")) return t;
        return "?";
    }

    private static string DetectNalTypes(byte[] data)
    {
        var sb = new System.Text.StringBuilder();
        int i = 0;
        while (i < data.Length - 4)
        {
            int adv = 0, nalByte = -1;
            if (data[i] == 0 && data[i+1] == 0 && data[i+2] == 0 && data[i+3] == 1)
            { adv = 4; if (i + 4 < data.Length) nalByte = data[i + 4]; }
            else if (data[i] == 0 && data[i+1] == 0 && data[i+2] == 1)
            { adv = 3; if (i + 3 < data.Length) nalByte = data[i + 3]; }
            if (adv > 0)
            {
                if (nalByte >= 0)
                {
                    int t = nalByte & 0x1F;
                    if (sb.Length > 0) sb.Append(' ');
                    sb.Append(t switch { 7 => "SPS", 8 => "PPS", 5 => "IDR", 1 => "P", 6 => "SEI", _ => $"NAL{t}" });
                }
                i += adv;
            }
            else i++;
        }
        return sb.Length > 0 ? sb.ToString() : "?";
    }

    private static byte[] EnsureAnnexB(byte[] data)
    {
        if (data.Length >= 4 && data[0] == 0 && data[1] == 0 && data[2] == 0 && data[3] == 1)
            return data;
        if (data.Length >= 3 && data[0] == 0 && data[1] == 0 && data[2] == 1)
            return data;
        var r = new byte[4 + data.Length];
        r[3] = 1;
        Buffer.BlockCopy(data, 0, r, 4, data.Length);
        return r;
    }

    // =========================================================================
    // Dispose
    // =========================================================================

    public async ValueTask DisposeAsync()
    {
        StopCapture();

        if (_audioEngine != null)
        {
            await _audioEngine.DisposeAsync();
            _audioEngine = null;
        }

        _adaptive?.Dispose();
        _adaptive = null;
        _avSync.Reset();
        _jitterBuf.Flush();

        _decoder?.Dispose();
        _decoder = null;
        _pc?.Close("dispose");
    }
}
