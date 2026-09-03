using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using SIPSorcery.Net;

namespace JaClipei.Client.Services;

/// <summary>
/// WebRTC peer-to-peer via SIPSorcery.
/// Vídeo = frames JPEG enviados por DataChannel (sem necessidade de codec nativo).
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

    /// <summary>Frame JPEG recebido pelo viewer.</summary>
    public event Action<byte[]>? FrameReceived;
    /// <summary>Candidato ICE local serializado em JSON — enviar via SignalR.</summary>
    public event Action<string>? IceCandidateReady;

    public bool IsDataChannelOpen => _dc?.readyState == RTCDataChannelState.open;

    // ── Sender (quem compartilha) ──────────────────────────────────────────

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

    // ── Receiver (quem assiste) ────────────────────────────────────────────

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

    // ── Captura (sender) ──────────────────────────────────────────────────

    public void StartCapture(int fps = 15)
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var delay = TimeSpan.FromMilliseconds(1000.0 / fps);

        Task.Run(async () =>
        {
            ScreenCaptureService.Initialize();
            while (!token.IsCancellationRequested)
            {
                var t0 = DateTime.UtcNow;
                try
                {
                    if (IsDataChannelOpen)
                    {
                        var raw = ScreenCaptureService.CaptureFrame(out int w, out int h);
                        if (raw != null)
                            _dc!.send(BgraToJpeg(raw, w, h));
                    }
                }
                catch { /* continua */ }

                var wait = delay - (DateTime.UtcNow - t0);
                if (wait > TimeSpan.Zero)
                    await Task.Delay(wait, token).ConfigureAwait(false);
            }
            ScreenCaptureService.Shutdown();
        }, token);
    }

    public void StopCapture()
    {
        _cts?.Cancel();
        _cts = null;
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
