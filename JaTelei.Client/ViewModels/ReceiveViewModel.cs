using System.IO;
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

    // Armazenado para poder desinscrever corretamente em DisposeAsync
    private readonly Action<string> _iceCandidateReadyHandler;

    private static readonly string LogPath =
        Path.Combine(Path.GetTempPath(), "jaclipei_error.txt");

    [ObservableProperty] private WriteableBitmap? _frame;
    [ObservableProperty] private string _statusText = "Conectando…";

    public event Action? StopRequested;

    public ReceiveViewModel(WebRtcService webRtc, SignalingService signaling, string fromUserId)
    {
        _webRtc = webRtc;
        _signaling = signaling;
        _fromUserId = fromUserId;

        _webRtc.FrameReceived   += OnFrameReceived;
        _webRtc.IceStateChanged += OnIceStateChanged;

        // Guarda referência ao lambda para poder desinscrever em DisposeAsync
        _iceCandidateReadyHandler = async c =>
            await _signaling.SendIceCandidateAsync(_fromUserId, c);
        _webRtc.IceCandidateReady += _iceCandidateReadyHandler;
    }

    /// <summary>Atualiza status com base no estado ICE.</summary>
    private void OnIceStateChanged(string state)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
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

    /// <summary>Recebe pixels BGRA brutos + dimensões vindos do WebRtcService.</summary>
    private void OnFrameReceived(byte[] bgra, int width, int height)
    {
        // Guard contra frames inválidos que crashariam o WriteableBitmap
        if (bgra == null || width <= 0 || height <= 0)
        {
            File.AppendAllText(LogPath,
                $"[ReceiveVM/Frame] {DateTime.Now}: frame inválido bgra={bgra?.Length ?? -1} {width}x{height}\n");
            return;
        }

        Application.Current.Dispatcher.Invoke(() =>
        {
            try
            {
                // Recria o WriteableBitmap somente se a resolução mudou
                if (Frame == null ||
                    Frame.PixelWidth  != width ||
                    Frame.PixelHeight != height)
                {
                    Frame = new WriteableBitmap(
                        width, height, 96, 96,
                        PixelFormats.Bgra32, null);
                    File.AppendAllText(LogPath,
                        $"[ReceiveVM/Frame] {DateTime.Now}: WriteableBitmap criado {width}x{height}\n");
                }

                int stride = width * 4;
                // Sanity check: bgra deve ter exatamente stride * height bytes
                if (bgra.Length < stride * height)
                {
                    File.AppendAllText(LogPath,
                        $"[ReceiveVM/Frame] {DateTime.Now}: bgra muito pequeno: {bgra.Length} < {stride * height}\n");
                    return;
                }

                Frame.Lock();
                Frame.WritePixels(
                    new Int32Rect(0, 0, width, height),
                    bgra, stride, 0);
                Frame.Unlock();

                StatusText = $"Recebendo — {width}×{height}";
            }
            catch (Exception ex)
            {
                // Log detalhado — anteriormente silencioso, o que escondia bugs reais
                File.AppendAllText(LogPath,
                    $"[ReceiveVM/Frame] {DateTime.Now}: ERRO {ex.GetType().Name}: {ex.Message}\n" +
                    $"  Frame={Frame?.PixelWidth}x{Frame?.PixelHeight} bgra={bgra.Length} {width}x{height}\n");
            }
        });
    }

    [RelayCommand]
    private void Stop() => StopRequested?.Invoke();

    public async ValueTask DisposeAsync()
    {
        _webRtc.FrameReceived     -= OnFrameReceived;
        _webRtc.IceStateChanged   -= OnIceStateChanged;
        _webRtc.IceCandidateReady -= _iceCandidateReadyHandler; // agora corretamente desinscrito
        await _webRtc.DisposeAsync();
    }
}
