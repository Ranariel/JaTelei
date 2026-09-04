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

    // Candidatos ICE que chegam antes de _pc ser criado são enfileirados aqui
    private readonly List<RTCIceCandidateInit> _pendingCandidates = new();
    private readonly object _candidateLock = new();

    // Contadores para logging por fase
    private int _framesRecv;

    private static readonly string LogPath =
        Path.Combine(Path.GetTempPath(), "jaclipei_error.txt");

    private static readonly RTCConfiguration Config = new()
    {
        iceServers = new List<RTCIceServer>
        {
            new() { urls = "stun:stun.l.google.com:19302" },
            new() { urls = "stun:stun1.l.google.com:19302" },
            // TURN UDP — funciona na maioria das redes
            new()
            {
                urls       = "turn:TURN_SERVER_REMOVED:3478",
                username   = "jatelei",
                credential = "TURN_CREDENTIAL_REMOVED"
            },
            // TURN TCP — fallback para redes que bloqueiam UDP
            new()
            {
                urls       = "turn:TURN_SERVER_REMOVED:3478?transport=tcp",
                username   = "jatelei",
                credential = "TURN_CREDENTIAL_REMOVED"
            },
        }
    };

    public event Action<byte[], int, int>?         FrameReceived;
    public event Action<string>?                   IceCandidateReady;
    /// <summary>Fired when the ICE connection state changes. Arg: human-readable state string.</summary>
    public event Action<string>?                   IceStateChanged;

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

        _pc.onicecandidate += c =>
        {
            var json = c.toJSON();
            var tipo = ExtractCandType(json);
            File.AppendAllText(LogPath, $"[ICE/Sender] {DateTime.Now}: candidato [{tipo}]: {json}\n");
            IceCandidateReady?.Invoke(json);
        };
        _pc.oniceconnectionstatechange += state =>
        {
            File.AppendAllText(LogPath, $"[ICE/Sender] {DateTime.Now}: iceState={state}\n");
            IceStateChanged?.Invoke(state.ToString());

            // ═══════════════════════════════════════════════════════════════
            // FIX CRÍTICO: forçar IDR quando o ICE *realmente* conecta.
            // SetRemoteAnswerAsync() chama ForceKeyframe() mas isso ocorre
            // antes da conectividade ICE ser estabelecida — em redes distintas
            // isso pode demorar vários segundos. Nesse intervalo o encoder
            // avança para P-frames e o decoder do receptor nunca vê SPS+PPS+IDR.
            // ═══════════════════════════════════════════════════════════════
            if (state == RTCIceConnectionState.connected ||
                state == RTCIceConnectionState.completed)
            {
                ScreenCaptureService.ForceKeyframe();
                File.AppendAllText(LogPath,
                    $"[Sender] {DateTime.Now}: ICE {state} → ForceKeyframe() disparado\n");
            }
        };
        _pc.onconnectionstatechange += state =>
            File.AppendAllText(LogPath, $"[ICE/Sender] {DateTime.Now}: connState={state}\n");

        var offer = _pc.createOffer();
        _pc.setLocalDescription(offer);
        File.AppendAllText(LogPath, $"[ICE/Sender] {DateTime.Now}: offer criado, ICE gathering iniciado\n");
        return await Task.FromResult(offer.sdp);
    }

    public Task SetRemoteAnswerAsync(string sdp)
    {
        _pc!.setRemoteDescription(new RTCSessionDescriptionInit
        {
            type = RTCSdpType.answer,
            sdp  = sdp
        });
        // Pre-prime do encoder: o ICE ainda não conectou, mas já garante que
        // o próximo grupo de NALs seja SPS+PPS+IDR (mesmo que chegue cedo demais
        // ao receptor, o ForceKeyframe no oniceconnectionstatechange é o definitivo).
        ScreenCaptureService.ForceKeyframe();
        File.AppendAllText(LogPath,
            $"[Sender] {DateTime.Now}: answer recebido → ForceKeyframe() pre-prime\n");
        return Task.CompletedTask;
    }

    // ── Receiver ──────────────────────────────────────────────────────────────

    public Task<string> CreateAnswerAsync(string offerSdp)
    {
        _pc = new RTCPeerConnection(Config);
        _framesRecv = 0;

        _decoder = new MfH264Decoder();

        _pc.OnVideoFrameReceived += (IPEndPoint ep, uint ts, byte[] frame, VideoFormat fmt) =>
        {
            try
            {
                // Garante formato Annex B (start code 0x00 0x00 0x00 0x01)
                var h264 = EnsureAnnexB(frame);

                // [DIAGNÓSTICO] Loga tipo dos NAL units recebidos.
                // Primeiros 10 frames + a cada 300 (≈10s @ 30fps) depois.
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
                            $"[Recv/Decode] {DateTime.Now}: frame#{_framesRecv-1} decodificado {w}x{h}\n");
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
            var tipo = ExtractCandType(json);
            File.AppendAllText(LogPath, $"[ICE/Recv] {DateTime.Now}: candidato [{tipo}]: {json}\n");
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

        // Aplica candidatos ICE que chegaram antes de _pc ser criado
        DrainPendingCandidates();

        File.AppendAllText(LogPath, $"[ICE/Recv] {DateTime.Now}: answer criado, ICE gathering iniciado\n");
        return Task.FromResult(answer.sdp);
    }

    // ── ICE ───────────────────────────────────────────────────────────────────

    public Task AddIceCandidateAsync(string candidateJson)
    {
        try
        {
            var init = JsonSerializer.Deserialize<RTCIceCandidateInit>(candidateJson)
                       ?? throw new ArgumentException("Candidato ICE inválido");
            lock (_candidateLock)
            {
                if (_pc == null)
                {
                    _pendingCandidates.Add(init);
                    File.AppendAllText(LogPath,
                        $"[ICE] {DateTime.Now}: candidato em fila (pc ainda nulo)\n");
                    return Task.CompletedTask;
                }
            }
            _pc.addIceCandidate(init);
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            File.AppendAllText(LogPath,
                $"[ICE/Add] {DateTime.Now}: {ex.GetType().Name}: {ex.Message}\n");
            return Task.CompletedTask;
        }
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
            try
            {
                _pc!.addIceCandidate(c);
                File.AppendAllText(LogPath,
                    $"[ICE] {DateTime.Now}: candidato pendente aplicado\n");
            }
            catch (Exception ex)
            {
                File.AppendAllText(LogPath,
                    $"[ICE/Drain] {DateTime.Now}: {ex.GetType().Name}: {ex.Message}\n");
            }
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Extrai "host", "srflx" ou "relay" da string de candidato ICE para logging.</summary>
    private static string ExtractCandType(string candidateJson)
    {
        foreach (var t in new[] { "relay", "srflx", "prflx", "host" })
            if (candidateJson.Contains($"typ {t}")) return t;
        return "?";
    }

    /// <summary>
    /// Detecta tipos de NAL unit em um buffer H264 Annex B.
    /// Retorna string como "SPS PPS IDR" ou "P" para logging de diagnóstico.
    /// </summary>
    private static string DetectNalTypes(byte[] data)
    {
        var sb = new System.Text.StringBuilder();
        int i  = 0;
        while (i < data.Length - 4)
        {
            int advance = 0;
            int nalByte = -1;

            if (data[i] == 0 && data[i+1] == 0 && data[i+2] == 0 && data[i+3] == 1)
            {
                advance = 4;
                if (i + 4 < data.Length) nalByte = data[i + 4];
            }
            else if (data[i] == 0 && data[i+1] == 0 && data[i+2] == 1)
            {
                advance = 3;
                if (i + 3 < data.Length) nalByte = data[i + 3];
            }

            if (advance > 0)
            {
                if (nalByte >= 0)
                {
                    int nalType = nalByte & 0x1F;
                    if (sb.Length > 0) sb.Append(' ');
                    sb.Append(nalType switch
                    {
                        7 => "SPS",
                        8 => "PPS",
                        5 => "IDR",
                        1 => "P",
                        6 => "SEI",
                        _ => $"NAL{nalType}"
                    });
                }
                i += advance;
            }
            else i++;
        }
        return sb.Length > 0 ? sb.ToString() : "?";
    }

    // ── Annex B ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Garante que o buffer H264 começa com start code Annex B.
    /// SIPSorcery 6.x às vezes entrega NAL units com start code (0x00 0x00 0x00 0x01)
    /// e às vezes sem. O decoder Windows MFT exige Annex B.
    /// </summary>
    private static byte[] EnsureAnnexB(byte[] data)
    {
        if (data.Length >= 4 &&
            data[0] == 0 && data[1] == 0 && data[2] == 0 && data[3] == 1)
            return data; // já tem start code de 4 bytes

        if (data.Length >= 3 &&
            data[0] == 0 && data[1] == 0 && data[2] == 1)
            return data; // já tem start code de 3 bytes

        // Prepend start code de 4 bytes
        var result = new byte[4 + data.Length];
        result[3] = 1; // 0x00 0x00 0x00 0x01
        Buffer.BlockCopy(data, 0, result, 4, data.Length);
        return result;
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
                try
                {
                    int dstH = targetHeight > 0 ? targetHeight : 0;
                    int dstW = 0; // mantém proporção
                    dllReady = ScreenCaptureService.Initialize(
                        dstWidth:    dstW,
                        dstHeight:   dstH,
                        fps:         effectiveFps,
                        bitrateKbps: bitrateKbps);

                    if (!dllReady)
                        File.AppendAllText(LogPath,
                            $"[Capture] {DateTime.Now}: ScreenCaptureService.Initialize() retornou false (DXGI/DLL falhou)\n");
                    else
                        File.AppendAllText(LogPath,
                            $"[Capture] {DateTime.Now}: DLL inicializada OK ({ScreenCaptureService.OutputWidth}x{ScreenCaptureService.OutputHeight})\n");
                }
                catch (Exception ex)
                {
                    File.AppendAllText(LogPath,
                        $"[Capture] {DateTime.Now}: Initialize exception: {ex.GetType().Name}: {ex.Message}\n");
                    // dllReady permanece false — nenhum frame será enviado pelo path nativo
                }
            }

            // Encoder C# — usado apenas para janela/região (não monitor inteiro)
            MfH264Encoder? fallbackEncoder = null;
            int encW = 0, encH = 0;
            int framesSent = 0;

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
                            try
                            {
                                _pc?.SendVideo(rtpDuration, h264);
                                framesSent++;
                                // Loga o primeiro frame enviado
                                if (framesSent == 1)
                                    File.AppendAllText(LogPath,
                                        $"[Capture] {DateTime.Now}: primeiro frame H264 enviado ({h264.Length} bytes)\n");
                            }
                            catch (Exception ex)
                            {
                                File.AppendAllText(LogPath,
                                    $"[Capture/Send] {DateTime.Now}: SendVideo falhou: {ex.GetType().Name}: {ex.Message}\n");
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
