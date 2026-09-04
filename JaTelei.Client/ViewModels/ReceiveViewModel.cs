using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JaTelei.Client.Services;

namespace JaTelei.Client.ViewModels;

public partial class ReceiveViewModel : ObservableObject, IAsyncDisposable
{
    private readonly WebRtcService _webRtc;
    private readonly SignalingService _signaling;
    private readonly string _fromUserId;

    private readonly Action<string> _iceCandidateReadyHandler;

    private static readonly string LogPath =
        Path.Combine(Path.GetTempPath(), "jaclipei_error.txt");

    [ObservableProperty] private WriteableBitmap? _frame;
    [ObservableProperty] private string _statusText  = "Conectando…";
    [ObservableProperty] private string _dimensionsText = "";
    [ObservableProperty] private bool   _isConnected  = false;

    // ── Volume ────────────────────────────────────────────────────────────

    private int  _volume           = 100;   // 0-100
    private bool _isMuted          = false;
    private int  _volumeBeforeMute = 100;

    public int Volume
    {
        get => _volume;
        set
        {
            if (_volume == value) return;
            _volume = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(MuteIcon));
            if (!_isMuted)
                SetSystemVolume(value);
        }
    }

    public string MuteIcon => _isMuted || _volume == 0 ? "🔇" : "🔊";

    // ── WinMM P/Invoke (volume mestre de saída de áudio) ─────────────────

    [DllImport("winmm.dll")] private static extern int waveOutSetVolume(IntPtr hwo, uint dwVolume);
    [DllImport("winmm.dll")] private static extern int waveOutGetVolume(IntPtr hwo, out uint dwVolume);

    private static void SetSystemVolume(int pct)
    {
        try
        {
            uint v = (uint)Math.Clamp(pct, 0, 100) * 65535 / 100;
            uint dword = (v & 0xFFFF) | ((v & 0xFFFF) << 16);
            waveOutSetVolume(IntPtr.Zero, dword);
        }
        catch { /* melhor esforço */ }
    }

    private static int GetSystemVolume()
    {
        try
        {
            waveOutGetVolume(IntPtr.Zero, out uint dword);
            return (int)((dword & 0xFFFF) * 100ul / 65535);
        }
        catch { return 100; }
    }

    // ─────────────────────────────────────────────────────────────────────

    public event Action? StopRequested;

    public ReceiveViewModel(WebRtcService webRtc, SignalingService signaling, string fromUserId)
    {
        _webRtc    = webRtc;
        _signaling = signaling;
        _fromUserId = fromUserId;

        _webRtc.FrameReceived   += OnFrameReceived;
        _webRtc.IceStateChanged += OnIceStateChanged;

        _iceCandidateReadyHandler = async c =>
            await _signaling.SendIceCandidateAsync(_fromUserId, c);
        _webRtc.IceCandidateReady += _iceCandidateReadyHandler;

        // Lê o volume atual do sistema para iniciar o slider no valor real
        _volume = GetSystemVolume();
    }

    // ── ICE state ─────────────────────────────────────────────────────────

    private void OnIceStateChanged(string state)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            IsConnected = state is "connected" or "completed";
            StatusText = state switch
            {
                "checking"     => "Estabelecendo conexão…",
                "connected"    => "Conectado — aguardando vídeo…",
                "completed"    => "Conectado — aguardando vídeo…",
                "disconnected" => "Conexão perdida",
                "failed"       => "Falha na conexão (NAT/firewall)",
                "closed"       => "Conexão encerrada",
                _              => $"ICE: {state}"
            };
        });
    }

    // ── Frame received ────────────────────────────────────────────────────

    private void OnFrameReceived(byte[] bgra, int width, int height)
    {
        if (bgra == null || width <= 0 || height <= 0)
        {
            File.AppendAllText(LogPath,
                $"[ReceiveVM/Frame] {DateTime.Now}: frame inválido bgra={bgra?.Length ?? -1} {width}x{height}\n");
            return;
        }

        // Usa InvokeAsync (não bloqueante) para não provocar dead-lock ou lag
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                if (Frame == null ||
                    Frame.PixelWidth  != width ||
                    Frame.PixelHeight != height)
                {
                    Frame = new WriteableBitmap(
                        width, height, 96, 96,
                        PixelFormats.Bgra32, null);
                }

                int stride = width * 4;
                if (bgra.Length < stride * height)
                {
                    File.AppendAllText(LogPath,
                        $"[ReceiveVM/Frame] {DateTime.Now}: bgra pequeno: {bgra.Length} < {stride * height}\n");
                    return;
                }

                Frame.Lock();
                Frame.WritePixels(new Int32Rect(0, 0, width, height), bgra, stride, 0);
                Frame.Unlock();

                IsConnected  = true;
                StatusText   = "Recebendo";
                DimensionsText = $"{width}×{height}";
            }
            catch (Exception ex)
            {
                File.AppendAllText(LogPath,
                    $"[ReceiveVM/Frame] {DateTime.Now}: ERRO {ex.GetType().Name}: {ex.Message}\n" +
                    $"  Frame={Frame?.PixelWidth}x{Frame?.PixelHeight} bgra={bgra.Length} {width}x{height}\n");
            }
        });
    }

    // ── Commands ──────────────────────────────────────────────────────────

    [RelayCommand]
    private void ToggleMute()
    {
        _isMuted = !_isMuted;
        if (_isMuted)
        {
            _volumeBeforeMute = _volume;
            SetSystemVolume(0);
        }
        else
        {
            SetSystemVolume(_volumeBeforeMute);
        }
        OnPropertyChanged(nameof(MuteIcon));
    }

    [RelayCommand]
    private void Stop() => StopRequested?.Invoke();

    // ── Dispose ───────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        _webRtc.FrameReceived     -= OnFrameReceived;
        _webRtc.IceStateChanged   -= OnIceStateChanged;
        _webRtc.IceCandidateReady -= _iceCandidateReadyHandler;
        await _webRtc.DisposeAsync();
    }
}
