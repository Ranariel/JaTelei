using System.Windows;
using System.Windows.Controls;
using JaClipei.Client.Services;

namespace JaClipei.Client.Views;

public enum ShareType { Screen, Window, Game }

public class ShareTarget
{
    public ShareType Type        { get; init; }
    public IntPtr    WindowHandle { get; init; }
    public string    DisplayName  { get; init; } = "";
    // For Screen: bounds (null = primary)
    public System.Windows.Rect? MonitorBounds { get; init; }
}

public partial class SharePickerDialog : Window
{
    public ShareTarget? Result { get; private set; }

    private ShareType _currentType;

    public SharePickerDialog()
    {
        InitializeComponent();
    }

    // ── Botões de tipo ────────────────────────────────────────────────────

    private void BtnTela_Click(object sender, RoutedEventArgs e)
    {
        _currentType = ShareType.Screen;
        SetSelected(BtnTela, BtnJanela, BtnJogo);

        var monitors = WindowEnumService.GetMonitors();
        if (monitors.Count <= 1)
        {
            // Apenas 1 monitor → seleciona direto, sem lista
            Result = new ShareTarget
            {
                Type        = ShareType.Screen,
                DisplayName = monitors.Count == 1 ? monitors[0].DisplayName : "Tela Principal"
            };
            BtnConfirm.IsEnabled = true;
            ListPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            // Múltiplos monitores → mostra lista
            ShowList("Selecione o monitor:", monitors.Select(m => (object)m).ToList());
        }
    }

    private void BtnJanela_Click(object sender, RoutedEventArgs e)
    {
        _currentType = ShareType.Window;
        SetSelected(BtnJanela, BtnTela, BtnJogo);
        var windows = WindowEnumService.GetVisibleWindows(excludeSelf: true);
        ShowList("Selecione a janela:", windows.Select(w => (object)w).ToList());
    }

    private void BtnJogo_Click(object sender, RoutedEventArgs e)
    {
        _currentType = ShareType.Game;
        SetSelected(BtnJogo, BtnTela, BtnJanela);
        // Jogo = mesma lista de janelas, mas label diferente
        var windows = WindowEnumService.GetVisibleWindows(excludeSelf: true);
        ShowList("Selecione o jogo em execução:", windows.Select(w => (object)w).ToList());
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private void SetSelected(System.Windows.Controls.Button selected,
                             params System.Windows.Controls.Button[] others)
    {
        selected.Style = (Style)Resources["TypeBtnSelected"];
        foreach (var b in others)
            b.Style = (Style)Resources["TypeBtn"];
    }

    private void ShowList(string label, List<object> items)
    {
        ListLabel.Text   = label;
        ItemsList.ItemsSource = items;
        ItemsList.SelectedItem = null;
        ListPanel.Visibility = Visibility.Visible;
        BtnConfirm.IsEnabled = false;
        Result = null;
    }

    private void ItemsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ItemsList.SelectedItem is null) { BtnConfirm.IsEnabled = false; return; }

        BtnConfirm.IsEnabled = true;

        switch (_currentType)
        {
            case ShareType.Screen when ItemsList.SelectedItem is MonitorInfo m:
                Result = new ShareTarget
                {
                    Type = ShareType.Screen,
                    DisplayName   = m.DisplayName,
                    MonitorBounds = m.Bounds
                };
                break;

            case ShareType.Window:
            case ShareType.Game:
                if (ItemsList.SelectedItem is WindowInfo w)
                    Result = new ShareTarget
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
        if (Result is not null)
            DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Result = null;
        DialogResult = false;
    }
}
