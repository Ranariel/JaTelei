using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using JaTelei.Client.Models;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace JaTelei.Client.Services;

/// <summary>
/// WebRTC peer-to-peer via SIPSorcery.
///
/// Sender pipeline (GPU-zero-copy, C++ DLL):
///   WGC / DXGI DDup → D3D11VP (BGRA→NV12) → NVENC/AMF/QSV → H.264/AV1 → SendVideo()
///
/// Receiver pipeline:
///   OnVideoFrameReceived → MfH264Decoder (HW-accelerated) → FrameReceived (BGRA)
/// </summary>
public class WebRtcService : IAsyncDisposable
{
    private RTCPeerConnection? _pc;
    private CancellationTokenSource? _cts;
    private MfH264Decoder? _decoder;

    // ICE candidates queued before _pc is created
    private readonly List<RTCIceCandidateInit> _pendingCandidates = new();
    private readonly object _candidateLock = new();

    private int _framesRecv;
    private int _framesSent;
    // Set by ICE "connected" when DLL path is unavailable; capture loop reads it to force IDR.
    private volatile bool _forceGdiKeyframe;

    private static readonly string LogPath =
        Path.Combine(Path.GetTempPath(), "jaclipei_error.txt");

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

    public event Action<byte[], int, int>? FrameReceived;
    public event Action<string>?           IceCandidateReady;
    public event Action<string>?           IceStateChanged;

    public bool IsConnected =>
        _pc?.iceConnectionState == RTCIceConnectionState.connected;

    // ── P/Invoke GDI (GDI fallback — used only when DLL init fails) ──────────

    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
    private const uint PW_RENDERFULLCONTENT = 0x00000002;

    // ── Sender ────────────────────────────────────────────────────────────────

    public async Task<string> CreateOfferAsync()
    {
        _pc = new RTCPeerConnection(RtcConfig);

        var videoTrack = new MediaStreamTrack(
            new List<VideoFormat> { new VideoFormat(VideoCodecsEnum.H264, 96) },
            MediaStreamStatusEnum.SendOnly);
        _pc.addTrack(videoTrack);

        _pc.onicecandidate += c =>
        {
            var json = c.toJSON();
            File.AppendAllText(LogPath,
                $"[ICE/Sender] {DateTime.Now}: [{ExtractCandType(json)}] {json}\n");
            IceCandidateReady?.Invoke(json);
        };

        _pc.oniceconnectionstatechange += state =>
        {
            File.AppendAllText(LogPath, $"[ICE/Sender] {DateTime.Now}: iceState={state}\n");
            IceStateChanged?.Invoke(state.ToString());

            // Force IDR when ICE is actually connected — P-frames before this
            // are useless to the receiver (no SPS+PPS+IDR seen yet).
            if (state == RTCIceConnectionState.connected)
            {
                ScreenCaptureService.ForceKeyframe(); // DLL path
                _forceGdiKeyframe = true;             // GDI fallback path
                File.AppendAllText(LogPath,
                    $"[Sender] {DateTime.Now}: ICE connected → ForceKeyframe()\n");
            }
        };

        _pc.onconnectionstatechange += state =>
            File.AppendAllText(LogPath, $"[ICE/Sender] {DateTime.Now}: connState={state}\n");

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
        // Pre-prime: forces SPS+PPS+IDR into next packet group.
        // The definitive ForceKeyframe fires on ICE "connected".
        ScreenCaptureService.ForceKeyframe();
        return Task.CompletedTask;
    }

    // ── Receiver ──────────────────────────────────────────────────────────────

    public Task<string> CreateAnswerAsync(string offerSdp)
    {
        _pc = new RTCPeerConnection(RtcConfig);
        _framesRecv = 0;
        _decoder = new MfH264Decoder();

        _pc.OnVideoFrameReceived += (IPEndPoint ep, uint ts, byte[] frame, VideoFormat fmt) =>
        {
            try
            {
                var h264 = EnsureAnnexB(frame);

                // Diagnostic logging: first 10 frames + every 300
                if (_framesRecv < 10 || _framesRecv % 300 == 0)
                {
                    var nalTypes = DetectNalTypes(h264);
                    File.AppendAllText(LogPath,
                        $"[Recv/NAL] {DateTime.Now}: frame#{_framesRecv} {h264.Length}B NALs=[{nalTypes}]\n");
                }
                _framesRecv++;

                var (bgra, w, h) = _decoder.Decode(h264);
                if (bgra != null)
                {
                    if (_framesRecv <= 5 || (_framesRecv - 1) % 300 == 0)
                        File.AppendAllText(LogPath,
                            $"[Recv/Decode] {DateTime.Now}: frame#{_framesRecv - 1} {w}x{h}\n");
                    FrameReceived?.Invoke(bgra, w, h);
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(LogPath,
                    $"[WebRTC/Decode] {DateTime.Now}: {ex.GetType().Name}: {ex.Message}\n");
            }
        };

        var videoTrack = new MediaStreamTrack(
            new List<VideoFormat> { new VideoFormat(VideoCodecsEnum.H264, 96) },
            MediaStreamStatusEnum.RecvOnly);
        _pc.addTrack(videoTrack);

        _pc.onicecandidate += c =>
        {
            var json = c.toJSON();
            File.AppendAllText(LogPath,
                $"[ICE/Recv] {DateTime.Now}: [{ExtractCandType(json)}] {json}\n");
            IceCandidateReady?.Invoke(json);
        };
        _pc.oniceconnectionstatechange += state =>
        {
            File.AppendAllText(LogPath, $"[ICE/Recv] {DateTime.Now}: iceState={state}\n");
            IceStateChanged?.Invoke(state.ToString());
        };
        _pc.onconnectionstatechange += state =>
            File.AppendAllText(LogPath, $"[ICE/Recv] {DateTime.Now}: connState={state}\n");

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

    // ── ICE ───────────────────────────────────────────────────────────────────

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
            File.AppendAllText(LogPath,
                $"[ICE/Add] {DateTime.Now}: {ex.GetType().Name}: {ex.Message}\n");
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
            catch (Exception ex)
            {
                File.AppendAllText(LogPath,
                    $"[ICE/Drain] {DateTime.Now}: {ex.GetType().Name}: {ex.Message}\n");
            }
        }
    }

    // ── Capture loop ──────────────────────────────────────────────────────────

    public void StartCapture(int fps = 30, ShareTarget? target = null)
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        int effectiveFps = target?.Fps > 0 ? target.Fps : fps;
        int targetHeight = target?.ResolutionHeight ?? 0;
        // Bitrate adaptivo: ~0,10 bits/pixel/frame, clamped [2–20 Mbps]
        int bitrateKbps  = ComputeBitrateKbps(
            targetHeight > 0 ? targetHeight * 16 / 9 : 1920,
            targetHeight > 0 ? targetHeight           : 1080,
            effectiveFps);

        uint rtpDuration = (uint)(90_000.0 / effectiveFps);
        var  delay       = TimeSpan.FromMilliseconds(1000.0 / effectiveFps);

        // Determine capture path:
        // - DLL path: monitor capture (DXGI DDup / WGC) OR window via WGC
        // - GDI fallback: only when DLL fails AND target is window/region
        _ = Task.Run(async () =>
        {
            bool dllReady = false;

            try
            {
                IntPtr hwnd = target?.WindowHandle ?? IntPtr.Zero;
                int dstH    = targetHeight > 0 ? targetHeight : 0;

                dllReady = ScreenCaptureService.Initialize(
                    dstWidth:     0,
                    dstHeight:    dstH,
                    fps:          effectiveFps,
                    bitrateKbps:  bitrateKbps,
                    windowHandle: hwnd,
                    captureMode:  JcCaptureMode.Auto,
                    codec:        JcCodec.H264,
                    encoderVendor: JcEncoderVendor.Auto,
                    enableAudio:  false);

                if (!dllReady)
                    File.AppendAllText(LogPath,
                        $"[Capture] {DateTime.Now}: DLL init failed — falling back to GDI\n");
                else
                    File.AppendAllText(LogPath,
                        $"[Capture] {DateTime.Now}: DLL OK ({ScreenCaptureService.OutputWidth}x" +
                        $"{ScreenCaptureService.OutputHeight})\n");
            }
            catch (Exception ex)
            {
                File.AppendAllText(LogPath,
                    $"[Capture] {DateTime.Now}: DLL init exception: {ex.GetType().Name}: {ex.Message}\n");
            }

            // GDI/software fallback encoder (only used when DLL init fails)
            MfH264Encoder? gdiEncoder = null;
            bool gdiEncoderFailed = false; // set on first failure — stops log flood
            int encW = 0, encH = 0;
            _framesSent = 0;

            try
            {
                while (!token.IsCancellationRequested)
                {
                    var t0 = DateTime.UtcNow;
                    try
                    {
                        byte[]? h264 = null;

                        if (dllReady)
                        {
                            // ─── Primary path: GPU pipeline via DLL ───────
                            var result = ScreenCaptureService.CaptureFrame();
                            h264 = result.Video;
                        }
                        else
                        {
                            // ─── GDI fallback: window / region ────────────
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
                                // Primary monitor / no specific bounds — capture full primary screen
                                int scrW = (int)System.Windows.SystemParameters.PrimaryScreenWidth;
                                int scrH = (int)System.Windows.SystemParameters.PrimaryScreenHeight;
                                raw = CaptureRegion(0, 0, scrW, scrH, out w, out h);
                            }

                            if (raw != null && w > 0 && h > 0)
                            {
                                // Compute target encoding resolution (maintain aspect ratio, align to 16)
                                // sws_scale inside MfH264Encoder handles the actual downscale — no GDI resize needed.
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
                                            File.AppendAllText(LogPath,
                                                $"[Capture/GdiEncoder] {DateTime.Now}: {ex.GetType().Name}: {ex.Message}\n" +
                                                $"  GDI encoder desativado. Verifique se o Media Feature Pack está instalado (Windows N/KN).\n");
                                        }
                                    }
                                    if (gdiEncoder != null)
                                    {
                                        if (_forceGdiKeyframe)
                                        {
                                            _forceGdiKeyframe = false;
                                            gdiEncoder.ForceKeyframe();
                                        }
                                        // Pass raw full-resolution capture; encoder's sws_scale downscales internally
                                        h264 = gdiEncoder.Encode(raw, w, h);
                                    }
                                }
                            }
                        }

                        if (h264?.Length > 0)
                        {
                            try
                            {
                                _pc?.SendVideo(rtpDuration, h264);
                                _framesSent++;
                                if (_framesSent == 1)
                                    File.AppendAllText(LogPath,
                                        $"[Capture] {DateTime.Now}: primeiro frame enviado ({h264.Length}B)\n");
                            }
                            catch (Exception ex)
                            {
                                File.AppendAllText(LogPath,
                                    $"[Capture/Send] {DateTime.Now}: SendVideo: {ex.GetType().Name}: {ex.Message}\n");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        File.AppendAllText(LogPath,
                            $"[Capture/Loop] {DateTime.Now}: {ex.GetType().Name}: {ex.Message}\n");
                    }

                    var wait = delay - (DateTime.UtcNow - t0);
                    if (wait > TimeSpan.Zero)
                        await Task.Delay(wait, token).ConfigureAwait(false);
                }
            }
            finally
            {
                gdiEncoder?.Dispose();
                if (dllReady) ScreenCaptureService.Shutdown();
            }
        }, token);
    }

    public void StopCapture()
    {
        _cts?.Cancel();
        _cts = null;
    }

    // ── GDI helpers (fallback only) ───────────────────────────────────────────

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
        var bd = bmp.LockBits(new Rectangle(0, 0, w, h),
                              ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var bytes = new byte[Math.Abs(bd.Stride) * h];
        Marshal.Copy(bd.Scan0, bytes, 0, bytes.Length);
        bmp.UnlockBits(bd);
        return bytes;
    }

    private static (byte[] bgra, int width, int height) ResizeBgra(
        byte[] bgra, int srcW, int srcH, int targetH)
    {
        if (targetH <= 0 || targetH >= srcH) return (bgra, srcW, srcH);
        int dstH = targetH;
        int dstW = (int)(srcW * ((double)dstH / srcH));
        if (dstW % 2 != 0) dstW--;
        if (dstH % 2 != 0) dstH--;
        using var src = new Bitmap(srcW, srcH, PixelFormat.Format32bppArgb);
        var bd = src.LockBits(new Rectangle(0, 0, srcW, srcH),
                              ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        Marshal.Copy(bgra, 0, bd.Scan0, bgra.Length);
        src.UnlockBits(bd);
        using var dst = new Bitmap(src, dstW, dstH);
        return (BitmapToBgra(dst, dstW, dstH), dstW, dstH);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Bitrate adaptivo para screen sharing: 0,10 bits/pixel/frame,
    /// clamped entre 2 Mbps e 20 Mbps.
    /// Ex: 1080p@30 → 6,2 Mbps | 1080p@60 → 12,4 Mbps | 720p@30 → 2,8 Mbps
    /// </summary>
    private static int ComputeBitrateKbps(int w, int h, int fps)
    {
        long bps = (long)w * h * fps / 10; // 0,10 bits/pixel/frame
        return (int)Math.Clamp(bps / 1000, 2_000, 20_000);
    }


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
            {
                adv = 4;
                if (i + 4 < data.Length) nalByte = data[i + 4];
            }
            else if (data[i] == 0 && data[i+1] == 0 && data[i+2] == 1)
            {
                adv = 3;
                if (i + 3 < data.Length) nalByte = data[i + 3];
            }
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

    // ── Dispose ───────────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        StopCapture();
        _decoder?.Dispose();
        _decoder = null;
        _pc?.Close("dispose");
        await ValueTask.CompletedTask;
    }
}
