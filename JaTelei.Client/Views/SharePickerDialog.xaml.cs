using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using JaTelei.Client.Models;
using JaTelei.Client.Services;

namespace JaTelei.Client.Views;

public partial class SharePickerDialog : Window
{
    public ShareTarget? Result { get; private set; }

    private ShareType _currentType;
    private ShareTarget? _partialResult;   // tipo + janela/monitor, sem fps/resolucao

    private int _selectedFps = 30;
    private int _selectedResolutionHeight = 720; // 0 = nativa

    // Preview state
    private CancellationTokenSource? _previewCts;

    // Opcoes de resolucao
    private record ResOption(string Label, int Height)
    {
        public override string ToString() => Label;
    }

    private static readonly IReadOnlyList<ResOption> Resolutions = new[]
    {
        new ResOption("Nativa  (sem redimensionamento)", 0),
        new ResOption("2160p - 4K",                     2160),
        new ResOption("1440p - 2K",                     1440),
        new ResOption("1080p - Full HD",                1080),
        new ResOption("720p - HD  (Recomendado)",       720),
        new ResOption("480p - SD",                      480),
        new ResOption("360p",                           360),
        new ResOption("240p",                           240),
        new ResOption("160p - LD",                      160),
    };

    // ─── GDI P/Invoke ────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rc);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);

    // ─────────────────────────────────────────────────────────────────────────

    public SharePickerDialog()
    {
        InitializeComponent();

        CboResolution.ItemsSource   = Resolutions;
        CboResolution.SelectedIndex = 4;   // 720p por padrao

        // Cancela capture de prévia ao fechar a janela para evitar leak
        Closed += (_, _) =>
        {
            _previewCts?.Cancel();
            _previewCts?.Dispose();
            _previewCts = null;
        };
    }

    // Botoes de tipo

    private void BtnTela_Click(object sender, RoutedEventArgs e)
    {
        _currentType = ShareType.Screen;
        SetSelected(BtnTela, BtnJanela, BtnJogo);

        var monitors = WindowEnumService.GetMonitors();
        if (monitors.Count <= 1)
        {
            _partialResult = new ShareTarget
            {
                Type        = ShareType.Screen,
                DisplayName = monitors.Count == 1 ? monitors[0].DisplayName : "Tela Principal"
            };
            BtnConfirm.IsEnabled   = true;
            ListPanel.Visibility   = Visibility.Collapsed;

            // Show preview for the single/primary monitor
            if (monitors.Count == 1)
                UpdatePreview(new ShareTarget { Type = ShareType.Screen, MonitorBounds = monitors[0].Bounds, DisplayName = monitors[0].DisplayName });
            else
                UpdatePreview(new ShareTarget { Type = ShareType.Screen, DisplayName = "Tela Principal" });
        }
        else
        {
            ShowList("Selecione o monitor:", monitors.Select(m => (object)m).ToList());
            HidePreview();
        }

        QualityPanel.Visibility = Visibility.Visible;
    }

    private void BtnJanela_Click(object sender, RoutedEventArgs e)
    {
        _currentType = ShareType.Window;
        SetSelected(BtnJanela, BtnTela, BtnJogo);
        var windows = WindowEnumService.GetVisibleWindows(excludeSelf: true);
        ShowList("Selecione a janela:", windows.Select(w => (object)w).ToList());
        QualityPanel.Visibility = Visibility.Visible;
        HidePreview();
    }

    private void BtnJogo_Click(object sender, RoutedEventArgs e)
    {
        _currentType = ShareType.Game;
        SetSelected(BtnJogo, BtnTela, BtnJanela);
        var windows = WindowEnumService.GetVisibleWindows(excludeSelf: true);
        ShowList("Selecione o jogo em execucao:", windows.Select(w => (object)w).ToList());
        QualityPanel.Visibility = Visibility.Visible;
        HidePreview();
    }

    // Qualidade: resolucao

    private void CboResolution_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CboResolution.SelectedItem is ResOption opt)
            _selectedResolutionHeight = opt.Height;
    }

    // Qualidade: FPS

    private void BtnFps_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (!int.TryParse(btn.Tag?.ToString(), out var fps)) return;

        _selectedFps = fps;

        BtnFps30.Style  = fps == 30  ? (Style)Resources["FpsBtnSelected"] : (Style)Resources["FpsBtn"];
        BtnFps60.Style  = fps == 60  ? (Style)Resources["FpsBtnSelected"] : (Style)Resources["FpsBtn"];
        BtnFps120.Style = fps == 120 ? (Style)Resources["FpsBtnSelected"] : (Style)Resources["FpsBtn"];
    }

    // Helpers

    private void SetSelected(System.Windows.Controls.Button selected,
                             params System.Windows.Controls.Button[] others)
    {
        selected.Style = (Style)Resources["TypeBtnSelected"];
        foreach (var b in others)
            b.Style = (Style)Resources["TypeBtn"];
    }

    private void ShowList(string label, List<object> items)
    {
        ListLabel.Text         = label;
        ItemsList.ItemsSource  = items;
        ItemsList.SelectedItem = null;
        ListPanel.Visibility   = Visibility.Visible;
        BtnConfirm.IsEnabled   = false;
        _partialResult = null;
    }

    private void ItemsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ItemsList.SelectedItem is null) { BtnConfirm.IsEnabled = false; return; }
        BtnConfirm.IsEnabled = true;

        switch (_currentType)
        {
            case ShareType.Screen when ItemsList.SelectedItem is MonitorInfo m:
                _partialResult = new ShareTarget
                {
                    Type          = ShareType.Screen,
                    DisplayName   = m.DisplayName,
                    MonitorBounds = m.Bounds
                };
                break;

            case ShareType.Window:
            case ShareType.Game:
                if (ItemsList.SelectedItem is WindowInfo w)
                    _partialResult = new ShareTarget
                    {
                        Type         = _currentType,
                        WindowHandle = w.Handle,
                        DisplayName  = w.Title
                    };
                break;
        }

        UpdatePreview(_partialResult);
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (_partialResult is null) return;

        Result = new ShareTarget
        {
            Type             = _partialResult.Type,
            WindowHandle     = _partialResult.WindowHandle,
            DisplayName      = _partialResult.DisplayName,
            MonitorBounds    = _partialResult.MonitorBounds,
            ResolutionHeight = _selectedResolutionHeight,
            Fps              = _selectedFps
        };

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Result = null;
        DialogResult = false;
    }

    // ─── Preview ─────────────────────────────────────────────────────────────

    private void HidePreview()
    {
        _previewCts?.Cancel();
        _previewCts = null;
        PreviewPanel.Visibility = Visibility.Collapsed;
        PreviewImage.Source = null;
    }

    private void UpdatePreview(ShareTarget? target)
    {
        // Cancel any previous capture in flight
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        var cts = new CancellationTokenSource();
        _previewCts = cts;

        if (target is null)
        {
            HidePreview();
            return;
        }

        // Show panel with loading text
        PreviewPanel.Visibility   = Visibility.Visible;
        PreviewImage.Source       = null;
        PreviewLoading.Visibility = Visibility.Visible;

        var token = cts.Token;
        Task.Run(() =>
        {
            BitmapSource? img = null;
            try
            {
                if (target.Type == ShareType.Screen)
                {
                    // Capture the monitor region (or primary screen as fallback)
                    if (target.MonitorBounds is System.Windows.Rect mb && mb.Width > 0 && mb.Height > 0)
                        img = CaptureRegionPreview((int)mb.X, (int)mb.Y, (int)mb.Width, (int)mb.Height);
                    else
                        img = CaptureRegionPreview(0, 0,
                            (int)SystemParameters.PrimaryScreenWidth,
                            (int)SystemParameters.PrimaryScreenHeight);
                }
                else if (target.WindowHandle != IntPtr.Zero)
                {
                    img = CaptureWindowPreview(target.WindowHandle);
                }
            }
            catch { /* best-effort */ }

            if (token.IsCancellationRequested) return;

            Dispatcher.Invoke(() =>
            {
                if (token.IsCancellationRequested) return;
                if (img != null)
                {
                    PreviewImage.Source       = img;
                    PreviewLoading.Visibility = Visibility.Collapsed;
                }
                else
                {
                    // Keep "Carregando prévia..." visible — couldn't capture
                }
            });
        }, token);
    }

    private BitmapSource? CaptureWindowPreview(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out RECT rc)) return null;
        int w = rc.Right  - rc.Left;
        int h = rc.Bottom - rc.Top;
        if (w <= 0 || h <= 0) return null;

        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            IntPtr hdc = g.GetHdc();
            bool ok = PrintWindow(hwnd, hdc, 0x02 /* PW_RENDERFULLCONTENT */);
            g.ReleaseHdc(hdc);
            if (!ok)
            {
                // Fallback: BitBlt from screen position
                g.CopyFromScreen(rc.Left, rc.Top, 0, 0, new System.Drawing.Size(w, h),
                                 CopyPixelOperation.SourceCopy);
            }
        }
        return BitmapToBitmapSource(bmp);
    }

    private BitmapSource? CaptureRegionPreview(int x, int y, int w, int h)
    {
        if (w <= 0 || h <= 0) return null;
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(w, h),
                             CopyPixelOperation.SourceCopy);
        return BitmapToBitmapSource(bmp);
    }

    private static BitmapSource BitmapToBitmapSource(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        ms.Seek(0, SeekOrigin.Begin);
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption  = BitmapCacheOption.OnLoad;
        bi.StreamSource = ms;
        bi.EndInit();
        bi.Freeze();   // make cross-thread-safe
        return bi;
    }
}
