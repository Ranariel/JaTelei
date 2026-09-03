using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JaClipei.Client.Services;

namespace JaClipei.Client.ViewModels;

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

    private void OnFrameReceived(byte[] jpegBytes)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            try
            {
                using var ms = new System.IO.MemoryStream(jpegBytes);
                var dec = BitmapDecoder.Create(ms,
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);
                var src = dec.Frames[0];

                if (Frame == null || Frame.PixelWidth != src.PixelWidth || Frame.PixelHeight != src.PixelHeight)
                    Frame = new WriteableBitmap(src.PixelWidth, src.PixelHeight, 96, 96, PixelFormats.Bgr32, null);

                var converted = new FormatConvertedBitmap(src, PixelFormats.Bgr32, null, 0);
                int stride = converted.PixelWidth * 4;
                var pixels = new byte[stride * converted.PixelHeight];
                converted.CopyPixels(pixels, stride, 0);

                Frame.Lock();
                Frame.WritePixels(new Int32Rect(0, 0, Frame.PixelWidth, Frame.PixelHeight), pixels, stride, 0);
                Frame.Unlock();

                StatusText = $"Recebendo — {converted.PixelWidth}×{converted.PixelHeight}";
            }
            catch { /* frame inválido */ }
        });
    }

    [RelayCommand]
    private void Stop()
    {
        StopRequested?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        _webRtc.FrameReceived -= OnFrameReceived;
        await _webRtc.DisposeAsync();
    }
}
