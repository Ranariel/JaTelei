using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using JaClipei.Client.Models;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Windows;

namespace JaClipei.Client.Services;

/// <summary>
/// WebRTC peer-to-peer via SIPSorcery.
/// Video = H.264 via MediaStreamTrack (Windows MediaFoundation).
/// </summary>
public class WebRtcService : IAsyncDisposable
{
    private RTCPeerConnection? _pc;
    private CancellationTokenSource? _cts;
    private WindowsVideoEndPoint? _videoSink;   // lado receptor: decodifica H264 → BGRA

    private static readonly RTCConfiguration Config = new()
    {
        iceServers = new List<RTCIceServer>
        {
            new() { urls = "stun:stun.l.google.com:19302" }
        }
    };

    /// <summary>Dispara com pixels BGRA brutos + dimensões (receptor).</summary>
    public event Action<byte[], int, int>? FrameReceived;
    public event Action<string>? IceCandidateReady;

    public bool IsVideoTrackReady =>
        _pc?.iceConnectionState == RTCIceConnectionState.connected ||
        _pc?.iceConnectionState == RTCIceConnectionState.completed;

    // ── P/Invoke GDI (captura de janelas) ─────────────────────────────────
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
    private const uint PW_RENDERFULLCONTENT = 0x00000002;

    // ── Sender ────────────────────────────────────────────────────────────

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
        return offer.sdp;
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

    // ── Receiver ──────────────────────────────────────────────────────────

    public Task<string> CreateAnswerAsync(string offerSdp)
    {
        _pc = new RTCPeerConnection(Config);

        // Endpoint que decodifica H264 → BGRA via Windows Media Foundation
        _videoSink = new WindowsVideoEndPoint();
        _videoSink.OnVideoSinkDecodedSample += (bmp, w, h, stride, pixFmt) =>
            FrameReceived?.Invoke(bmp, (int)w, (int)h);

        // Repassa cada pacote RTP de vídeo ao decodificador
        _pc.OnRtpPacketReceived += (ep, media, packet) =>
        {
            if (media == SDPMediaTypesEnum.video)
                _videoSink.GotVideoRtp(ep,
                    packet.Header.SyncSource,
                    packet.Header.SequenceNumber,
                    packet.Header.Timestamp,
                    packet.Header.PayloadType,
                    packet.Header.MarkerBit == 1,
                    packet.Payload);
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

    // ── ICE ───────────────────────────────────────────────────────────────

    public Task AddIceCandidateAsync(string candidateJson)
    {
        var init = JsonSerializer.Deserialize<RTCIceCandidateInit>(candidateJson)
                   ?? throw new ArgumentException("Candidato ICE invalido");
        _pc!.addIceCandidate(init);
        return Task.CompletedTask;
    }

    // ── Captura ───────────────────────────────────────────────────────────

    public void StartCapture(int fps = 15, ShareTarget? target = null)
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        int effectiveFps     = target?.Fps > 0 ? target.Fps : fps;
        int resolutionHeight = target?.ResolutionHeight ?? 720;
        var delay            = TimeSpan.FromMilliseconds(1000.0 / effectiveFps);
        // H.264 clock: 90 000 Hz
        uint rtpDuration     = (uint)(90_000.0 / effectiveFps);

        bool useNative = target is null ||
                         (target.WindowHandle == IntPtr.Zero && target.MonitorBounds is null);

        Task.Run(async () =>
        {
            if (useNative) ScreenCaptureService.Initialize();

            // Endpoint com encoder H264 via Windows MF, em modo "fonte externa"
            using var videoSource = new WindowsVideoEndPoint();
            videoSource.OnVideoSourceEncodedSample += (duration, sample, _) =>
            {
                try { _pc?.SendVideo(duration, sample); }
                catch { /* conexão ainda não pronta */ }
            };

            while (!token.IsCancellationRequested)
            {
                var t0 = DateTime.UtcNow;
                try
                {
                    byte[]? raw = null;
                    int w = 0, h = 0;

                    if (useNative)
                        raw = ScreenCaptureService.CaptureFrame(out w, out h);
                    else if (target!.WindowHandle != IntPtr.Zero)
                        raw = CaptureWindow(target.WindowHandle, out w, out h);
                    else if (target!.MonitorBounds is System.Windows.Rect bounds)
                        raw = CaptureRegion((int)bounds.X, (int)bounds.Y,
                                            (int)bounds.Width, (int)bounds.Height,
                                            out w, out h);

                    if (raw != null && w > 0 && h > 0)
                    {
                        var (bgra, dstW, dstH) = ResizeBgra(raw, w, h, resolutionHeight);
                        // Alimenta o encoder; resultado chega em OnVideoSourceEncodedSample
                        videoSource.ExternalVideoSourceRawSample(
                            (uint)(1000.0 / effectiveFps),
                            dstW, dstH,
                            bgra,
                            VideoPixelFormatsEnum.Bgra);
                    }
                }
                catch { /* tolera falhas isoladas de captura */ }

                var wait = delay - (DateTime.UtcNow - t0);
                if (wait > TimeSpan.Zero)
                    await Task.Delay(wait, token).ConfigureAwait(false);
            }

            if (useNative) ScreenCaptureService.Shutdown();
        }, token);
    }

    public void StopCapture()
    {
        _cts?.Cancel();
        _cts = null;
    }

    // ── Captura GDI ───────────────────────────────────────────────────────

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
            g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(w, h));
        return BitmapToBgra(bmp, w, h);
    }

    private static byte[] BitmapToBgra(Bitmap bmp, int w, int h)
    {
        var bd = bmp.LockBits(new Rectangle(0, 0, w, h),
                              ImageLockMode.ReadOnly,
                              PixelFormat.Format32bppArgb);
        var bytes = new byte[Math.Abs(bd.Stride) * h];
        Marshal.Copy(bd.Scan0, bytes, 0, bytes.Length);
        bmp.UnlockBits(bd);
        return bytes;
    }

    // ── Redimensiona BGRA mantendo proporção ──────────────────────────────

    private static (byte[] bgra, int width, int height) ResizeBgra(
        byte[] bgra, int srcW, int srcH, int targetH)
    {
        if (targetH <= 0 || targetH >= srcH)
            return (bgra, srcW, srcH);

        int dstH = targetH;
        int dstW = (int)(srcW * ((double)dstH / srcH));
        // H.264: dimensões devem ser pares
        if (dstW % 2 != 0) dstW--;
        if (dstH % 2 != 0) dstH--;

        using var src = new Bitmap(srcW, srcH, PixelFormat.Format32bppArgb);
        var bd = src.LockBits(new Rectangle(0, 0, srcW, srcH),
                              ImageLockMode.WriteOnly,
                              PixelFormat.Format32bppArgb);
        Marshal.Copy(bgra, 0, bd.Scan0, bgra.Length);
        src.UnlockBits(bd);

        using var dst = new Bitmap(src, dstW, dstH);
        return (BitmapToBgra(dst, dstW, dstH), dstW, dstH);
    }

    public async ValueTask DisposeAsync()
    {
        StopCapture();
        _videoSink?.Dispose();
        _pc?.Close("dispose");
        await ValueTask.CompletedTask;
    }
}
