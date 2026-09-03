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
/// Sender: JaTelei.Capture.dll (full-GPU: DXGI→D3D11VP→HW H264) → RTCPeerConnection.SendVideo().
/// Receiver: RTCPeerConnection.OnVideoFrameReceived → MfH264Decoder → FrameReceived.
/// </summary>
public class WebRtcService : IAsyncDisposable
{
    private RTCPeerConnection? _pc;
    private CancellationTokenSource? _cts;
    private MfH264Decoder? _decoder;

    private static readonly string LogPath =
        Path.Combine(Path.GetTempPath(), "jaclipei_error.txt");

    private static readonly RTCConfiguration Config = new()
    {
        iceServers = new List<RTCIceServer>
        {
            new() { urls = "stun:stun.l.google.com:19302" }
        }
    };

    public event Action<byte[], int, int>? FrameReceived;
    public event Action<string>?           IceCandidateReady;

    public bool IsConnected =>
        _pc?.iceConnectionState == RTCIceConnectionState.connected;

    // ── P/Invoke GDI (captura de janela/região — fallback quando C++ não é usado) ──

    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
    private const uint PW_RENDERFULLCONTENT = 0x00000002;

    // ── Sender ────────────────────────────────────────────────────────────────

    public async Task<string> CreateOfferAsync()
    {
        _pc = new RTCPeerConnection(Config);

        var videoTrack = new MediaStreamTrack(
            new List<VideoFormat> { new VideoFormat(VideoCodecsEnum.H264, 96) },
            MediaStreamStatusEnum.SendOnly);
        _pc.addTrack(videoTrack);

        _pc.onicecandidate += c => IceCandidateReady?.Invoke(c.toJSON());

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
        // Pede keyframe ao encoder assim que o peer aceita
        ScreenCaptureService.ForceKeyframe();
        return Task.CompletedTask;
    }

    // ── Receiver ──────────────────────────────────────────────────────────────

    public Task<string> CreateAnswerAsync(string offerSdp)
    {
        _pc = new RTCPeerConnection(Config);

        _decoder = new MfH264Decoder();

        _pc.OnVideoFrameReceived += (IPEndPoint ep, uint ts, byte[] frame, VideoFormat fmt) =>
        {
            try
            {
                // Decode() auto-detects resolution from H264 SPS — no fixed size needed
                var (bgra, w, h) = _decoder.Decode(frame);
                if (bgra != null)
                    FrameReceived?.Invoke(bgra, w, h);
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

        _pc.onicecandidate += c => IceCandidateReady?.Invoke(c.toJSON());
        _pc.setRemoteDescription(new RTCSessionDescriptionInit
        {
            type = RTCSdpType.offer,
            sdp  = offerSdp
        });
        var answer = _pc.createAnswer();
        _pc.setLocalDescription(answer);
        return Task.FromResult(answer.sdp);
    }

    // ── ICE ───────────────────────────────────────────────────────────────────

    public Task AddIceCandidateAsync(string candidateJson)
    {
        var init = JsonSerializer.Deserialize<RTCIceCandidateInit>(candidateJson)
                   ?? throw new ArgumentException("Candidato ICE inválido");
        _pc!.addIceCandidate(init);
        return Task.CompletedTask;
    }

    // ── Captura full-GPU (monitor inteiro via DLL C++) ────────────────────────

    public void StartCapture(int fps = 30, ShareTarget? target = null)
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        int effectiveFps = target?.Fps > 0 ? target.Fps : fps;
        int bitrateKbps  = 8_000; // 8 Mbps — ajustável depois via RTCP
        int targetHeight = target?.ResolutionHeight ?? 0; // 0 = nativo

        // H264 RTP clock = 90 000 Hz
        uint rtpDuration = (uint)(90_000.0 / effectiveFps);
        var  delay       = TimeSpan.FromMilliseconds(1000.0 / effectiveFps);

        bool useNativeDll = target is null ||
                            (target.WindowHandle == IntPtr.Zero && target.MonitorBounds is null);

        Task.Run(async () =>
        {
            bool dllReady = false;

            if (useNativeDll)
            {
                int dstH = targetHeight > 0 ? targetHeight : 0;
                int dstW = 0; // mantém proporção
                dllReady = ScreenCaptureService.Initialize(
                    dstWidth:    dstW,
                    dstHeight:   dstH,
                    fps:         effectiveFps,
                    bitrateKbps: bitrateKbps);
            }

            // Encoder C# — usado apenas para janela/região (não monitor inteiro)
            MfH264Encoder? fallbackEncoder = null;
            int encW = 0, encH = 0;

            try
            {
                while (!token.IsCancellationRequested)
                {
                    var t0 = DateTime.UtcNow;
                    try
                    {
                        byte[]? h264 = null;

                        if (useNativeDll && dllReady)
                        {
                            // ─── Path principal: pipeline full-GPU via DLL ───
                            h264 = ScreenCaptureService.CaptureAndEncode();
                        }
                        else
                        {
                            // ─── Fallback: GDI + encoder C# (janela/região) ─
                            byte[]? raw = null;
                            int w = 0, h = 0;

                            if (target!.WindowHandle != IntPtr.Zero)
                                raw = CaptureWindow(target.WindowHandle, out w, out h);
                            else if (target.MonitorBounds is System.Windows.Rect bounds)
                                raw = CaptureRegion((int)bounds.X, (int)bounds.Y,
                                                    (int)bounds.Width, (int)bounds.Height,
                                                    out w, out h);

                            if (raw != null && w > 0 && h > 0)
                            {
                                int dstH = targetHeight > 0 ? targetHeight : h;
                                var (bgra, dstW, dstHH) = ResizeBgra(raw, w, h, dstH);

                                if (fallbackEncoder == null || dstW != encW || dstHH != encH)
                                {
                                    fallbackEncoder?.Dispose();
                                    fallbackEncoder = new MfH264Encoder(dstW, dstHH, effectiveFps, bitrateKbps * 1000);
                                    encW = dstW; encH = dstHH;
                                }

                                h264 = fallbackEncoder.Encode(bgra, dstW, dstHH);
                            }
                        }

                        if (h264?.Length > 0)
                        {
                            try { _pc?.SendVideo(rtpDuration, h264); }
                            catch { }
                        }
                    }
                    catch { }

                    var wait = delay - (DateTime.UtcNow - t0);
                    if (wait > TimeSpan.Zero)
                        await Task.Delay(wait, token).ConfigureAwait(false);
                }
            }
            finally
            {
                fallbackEncoder?.Dispose();
                if (useNativeDll && dllReady)
                    ScreenCaptureService.Shutdown();
            }
        }, token);
    }

    public void StopCapture()
    {
        _cts?.Cancel();
        _cts = null;
    }

    // ── GDI (fallback para janela/região) ────────────────────────────────────

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

    public async ValueTask DisposeAsync()
    {
        StopCapture();
        _decoder?.Dispose();
        _decoder = null;
        _pc?.Close("dispose");
        await ValueTask.CompletedTask;
    }
}
