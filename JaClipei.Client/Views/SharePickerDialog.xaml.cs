using System.Windows;
using System.Windows.Controls;
using JaClipei.Client.Models;
using JaClipei.Client.Services;

namespace JaClipei.Client.Views;

public partial class SharePickerDialog : Window
{
    public ShareTarget? Result { get; private set; }

    private ShareType _currentType;
    private ShareTarget? _partialResult;   // tipo + janela/monitor, sem fps/resolucao

    private int _selectedFps = 30;
    private int _selectedResolutionHeight = 720; // 0 = nativa

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

    public SharePickerDialog()
    {
        InitializeComponent();

        CboResolution.ItemsSource   = Resolutions;
        CboResolution.SelectedIndex = 4;   // 720p por padrao
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
        }
        else
        {
            ShowList("Selecione o monitor:", monitors.Select(m => (object)m).ToList());
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
    }

    private void BtnJogo_Click(object sender, RoutedEventArgs e)
    {
        _currentType = ShareType.Game;
        SetSelected(BtnJogo, BtnTela, BtnJanela);
        var windows = WindowEnumService.GetVisibleWindows(excludeSelf: true);
        ShowList("Selecione o jogo em execucao:", windows.Select(w => (object)w).ToList());
        QualityPanel.Visibility = Visibility.Visible;
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
}
