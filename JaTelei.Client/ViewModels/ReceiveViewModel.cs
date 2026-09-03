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

    [ObservableProperty] private WriteableBitmap? _frame;
    [ObservableProperty] private string _statusText = "Conectando…";

    public event Action? StopRequested;

    public ReceiveViewModel(WebRtcService webRtc, SignalingService signaling, string fromUserId)
    {
        _webRtc = webRtc;
        _signaling = signaling;
        _fromUserId = fromUserId;

        _webRtc.FrameReceived += OnFrameReceived;
        _webRtc.IceCandidateReady += async c =>
            await _signaling.SendIceCandidateAsync(_fromUserId, c);
    }

    /// <summary>Recebe pixels BGRA brutos + dimensões vindos do WebRtcService.</summary>
    private void OnFrameReceived(byte[] bgra, int width, int height)
    {
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
                }

                int stride = width * 4;
                Frame.Lock();
                Frame.WritePixels(
                    new Int32Rect(0, 0, width, height),
                    bgra, stride, 0);
                Frame.Unlock();

                StatusText = $"Recebendo — {width}×{height}";
            }
            catch { /* frame inválido — ignora */ }
        });
    }

    [RelayCommand]
    private void Stop() => StopRequested?.Invoke();

    public async ValueTask DisposeAsync()
    {
        _webRtc.FrameReceived -= OnFrameReceived;
        await _webRtc.DisposeAsync();
    }
}
