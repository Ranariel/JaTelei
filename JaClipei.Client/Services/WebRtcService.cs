using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using JaClipei.Client.Models;
using SIPSorcery.Net;

namespace JaClipei.Client.Services;

/// <summary>
/// WebRTC peer-to-peer via SIPSorcery.
/// Vídeo = frames JPEG enviados por DataChannel.
/// </summary>
public class WebRtcService : IAsyncDisposable
{
    private RTCPeerConnection? _pc;
    private RTCDataChannel? _dc;
    private CancellationTokenSource? _cts;

    private static readonly RTCConfiguration Config = new()
    {
        iceServers = new List<RTCIceServer>
        {
            new() { urls = "stun:stun.l.google.com:19302" }
        }
    };

    public event Action<byte[]>? FrameReceived;
    public event Action<string>? IceCandidateReady;

    public bool IsDataChannelOpen => _dc?.readyState == RTCDataChannelState.open;

    // ── P/Invoke para captura de janela ───────────────────────────────────

    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private const uint PW_RENDERFULLCONTENT = 0x00000002;

    // ── Sender ────────────────────────────────────────────────────────────

    public async Task<string> CreateOfferAsync()
    {
        _pc = new RTCPeerConnection(Config);
        _dc = await _pc.createDataChannel("screenshare");
        _pc.onicecandidate += c => IceCandidateReady?.Invoke(c.toJSON());
        var offer = _pc.createOffer();
        _pc.setLocalDescription(offer);
        return offer.sdp;
    }

    public Task SetRemoteAnswerAsync(string sdp)
    {
        _pc!.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = sdp });
        return Task.CompletedTask;
    }

    // ── Receiver ──────────────────────────────────────────────────────────

    public Task<string> CreateAnswerAsync(string offerSdp)
    {
        _pc = new RTCPeerConnection(Config);
        _pc.ondatachannel += ch =>
        {
            _dc = ch;
            ch.onmessage += (_, _, data) => FrameReceived?.Invoke(data);
        };
        _pc.onicecandidate += c => IceCandidateReady?.Invoke(c.toJSON());
        _pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = offerSdp });
        var answer = _pc.createAnswer();
        _pc.setLocalDescription(answer);
        return Task.FromResult(answer.sdp);
    }

    // ── ICE ───────────────────────────────────────────────────────────────

    public Task AddIceCandidateAsync(string candidateJson)
    {
        var init = JsonSerializer.Deserialize<RTCIceCandidateInit>(candidateJson)
                   ?? throw new ArgumentException("Candidato ICE inválido");
        _pc!.addIceCandidate(init);
        return Task.CompletedTask;
    }

    // ── Captura ───────────────────────────────────────────────────────────

    public void StartCapture(int fps = 15, ShareTarget? target = null)
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var delay = TimeSpan.FromMilliseconds(1000.0 / fps);

        Task.Run(async () =>
        {
            bool useNative = target is null ||
                             (target.WindowHandle == IntPtr.Zero && target.MonitorBounds is null);

            if (useNative) ScreenCaptureService.Initialize();

            while (!token.IsCancellationRequested)
            {
                var t0 = DateTime.UtcNow;
                try
                {
                    if (IsDataChannelOpen)
                    {
                        byte[]? raw = null;
                        int w = 0, h = 0;

                        if (useNative)
                            raw = ScreenCaptureService.CaptureFrame(out w, out h);
                        else if (target!.WindowHandle != IntPtr.Zero)
                            raw = CaptureWindow(target.WindowHandle, out w, out h);
                        else if (target!.MonitorBounds is System.Windows.Rect bounds)
                            raw = CaptureRegion((int)bounds.X, (int)bounds.Y, (int)bounds.Width, (int)bounds.Height, out w, out h);

                        if (raw != null && w > 0 && h > 0)
                            _dc!.send(BgraToJpeg(raw, w, h));
                    }
                }
                catch { /* continua */ }

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

    // ── Captura GDI de janela ─────────────────────────────────────────────

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
        width  = w;
        height = h;
        if (w <= 0 || h <= 0) { width = 1; height = 1; return new byte[4]; }

        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(w, h));

        return BitmapToBgra(bmp, w, h);
    }

    private static byte[] BitmapToBgra(Bitmap bmp, int w, int h)
    {
        var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var bytes = new byte[Math.Abs(bd.Stride) * h];
        Marshal.Copy(bd.Scan0, bytes, 0, bytes.Length);
        bmp.UnlockBits(bd);
        return bytes;
    }

    // ── Encoder BGRA → JPEG ───────────────────────────────────────────────

    private static readonly ImageCodecInfo JpegCodec =
        ImageCodecInfo.GetImageEncoders().First(e => e.MimeType == "image/jpeg");

    private static byte[] BgraToJpeg(byte[] bgra, int width, int height, int quality = 55)
    {
        int dstW = Math.Min(width, 1280);
        int dstH = (int)(height * ((double)dstW / width));

        using var src = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var bd = src.LockBits(new Rectangle(0, 0, width, height),
                              ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        Marshal.Copy(bgra, 0, bd.Scan0, bgra.Length);
        src.UnlockBits(bd);

        using var dst = new Bitmap(src, dstW, dstH);
        var enc = new EncoderParameters(1);
        enc.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);

        using var ms = new System.IO.MemoryStream();
        dst.Save(ms, JpegCodec, enc);
        return ms.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        StopCapture();
        _pc?.Close("dispose");
        await ValueTask.CompletedTask;
    }
}
