using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using JaTelei.Client.Models;
using JaTelei.Client.Services;
using JaTelei.Client.ViewModels;
using JaTelei.Client.Views;

namespace JaTelei.Client;

public partial class MainWindow : Window
{
    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "jaclipei_error.txt");
    private readonly ApiService _api = new();
    private readonly SignalingService _signaling = new();
    private UpdateService.UpdateInfo? _pendingUpdate;

    public MainWindow()
    {
        InitializeComponent();

        // Ajusta borda/padding ao maximizar para não cobrir a barra de tarefas
        StateChanged += OnStateChanged;

        ShowLogin();
        _ = CheckForUpdateAsync();
    }

    // ── Gerenciamento da janela ────────────────────────────────────────────

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            // Evita cobrir a barra de tarefas do Windows
            RootBorder.BorderThickness = new Thickness(0);
            BtnMaxRestore.Content  = "❐";
            BtnMaxRestore.ToolTip  = "Restaurar";
        }
        else
        {
            RootBorder.BorderThickness = new Thickness(1);
            BtnMaxRestore.Content  = "□";
            BtnMaxRestore.ToolTip  = "Maximizar";
        }
    }

    private void Minimize_Click(object s, RoutedEventArgs e)  => WindowState = WindowState.Minimized;
    private void Close_Click(object s, RoutedEventArgs e)     => Close();

    private void MaxRestore_Click(object s, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    // ── Atualização ────────────────────────────────────────────────────────

    private async Task CheckForUpdateAsync()
    {
        var update = await UpdateService.CheckAsync(AppVersion.Current);
        if (update is null) return;
        _pendingUpdate = update;
        Dispatcher.Invoke(() =>
        {
            UpdateText.Text = $"Nova versão {update.Version} disponível!";
            UpdateBanner.Visibility = Visibility.Visible;
        });
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate is null) return;
        UpdateButton.IsEnabled = false;
        UpdateText.Text = "Baixando atualização...";
        await UpdateService.DownloadAndRestartAsync(_pendingUpdate);
    }

    // ── Login ──────────────────────────────────────────────────────────────

    private void ShowLogin()
    {
        try
        {
            var vm = new LoginViewModel(_api);
            vm.LoginSuccess += async () =>
            {
                try
                {
                    await _signaling.ConnectAsync(_api.Token!);
                    _signaling.OfferReceived += OnOfferReceived;
                }
                catch (Exception ex)
                {
                    File.AppendAllText(LogPath, $"[SignalR] {DateTime.Now}: {ex}\n\n");
                }
                Dispatcher.Invoke(ShowFriends);
            };
            MainContent.Content = new LoginView { DataContext = vm };
        }
        catch (Exception ex)
        {
            File.AppendAllText(LogPath, $"[ShowLogin] {DateTime.Now}: {ex}\n\n");
            MainContent.Content = new TextBlock
            {
                Text = $"ERRO LOGIN: {ex.GetType().Name}\n{ex.Message}",
                Foreground = new SolidColorBrush(Colors.Red),
                FontSize = 16,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(20)
            };
        }
    }

    // ── Friends ────────────────────────────────────────────────────────────

    private void ShowFriends()
    {
        // Descarta ReceiveViewModel anterior se houver
        if (MainContent.Content is ReceiveView rv &&
            rv.DataContext is ReceiveViewModel rvm)
        {
            _ = rvm.DisposeAsync().AsTask();
        }

        try
        {
            var vm = new FriendsViewModel(_api, _signaling);
            vm.StartShareRequested += friend => Dispatcher.Invoke(() => ShowSharePicker(friend));
            _ = vm.LoadCommand.ExecuteAsync(null);
            MainContent.Content = new FriendsView { DataContext = vm };
        }
        catch (Exception ex)
        {
            File.AppendAllText(LogPath, $"[ShowFriends] {DateTime.Now}: {ex}\n\n");
            MainContent.Content = new TextBlock
            {
                Text = $"ERRO: {ex.GetType().Name}\n{ex.Message}",
                Foreground = new SolidColorBrush(Colors.Red),
                FontSize = 16,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(20)
            };
        }
    }

    // ── Share Picker ───────────────────────────────────────────────────────

    private void ShowSharePicker(Friend friend)
    {
        try
        {
            var dialog = new SharePickerDialog { Owner = this };
            if (dialog.ShowDialog() == true && dialog.Result is ShareTarget target)
                _ = StartSendingAsync(friend, target);
        }
        catch (Exception ex)
        {
            File.AppendAllText(LogPath, $"[SharePicker] {DateTime.Now}: {ex}\n\n");
            MessageBox.Show($"Erro ao abrir seletor:\n{ex.Message}", "Erro",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ── Sender side ────────────────────────────────────────────────────────

    private async Task StartSendingAsync(Friend friend, ShareTarget target)
    {
        var webRtc = new WebRtcService();
        var friendId = friend.Id.ToString();

        Action<string> iceCandidateReadyHandler = async c =>
            await _signaling.SendIceCandidateAsync(friendId, c);
        webRtc.IceCandidateReady += iceCandidateReadyHandler;

        Action<string, string>? answerReceivedHandler = null;
        answerReceivedHandler = async (from, sdp) =>
        {
            if (from != friendId) return;
            try { await webRtc.SetRemoteAnswerAsync(sdp); }
            catch (Exception ex)
            {
                File.AppendAllText(LogPath,
                    $"[Answer/Sender] {DateTime.Now}: {ex.GetType().Name}: {ex.Message}\n");
            }
        };
        _signaling.AnswerReceived += answerReceivedHandler;

        Action<string, string>? iceCandReceivedHandler = null;
        iceCandReceivedHandler = async (from, cand) =>
        {
            if (from != friendId) return;
            try { await webRtc.AddIceCandidateAsync(cand); }
            catch (Exception ex)
            {
                File.AppendAllText(LogPath,
                    $"[ICE/Sender] {DateTime.Now}: {ex.GetType().Name}: {ex.Message}\n");
            }
        };
        _signaling.IceCandidateReceived += iceCandReceivedHandler;

        var offerSdp = await webRtc.CreateOfferAsync();
        await _signaling.SendOfferAsync(friendId, offerSdp);

        webRtc.StartCapture(target: target);

        webRtc.IceStateChanged += state =>
        {
            if (state == "closed" || state == "failed")
            {
                webRtc.IceCandidateReady       -= iceCandidateReadyHandler;
                _signaling.AnswerReceived       -= answerReceivedHandler;
                _signaling.IceCandidateReceived -= iceCandReceivedHandler;
                File.AppendAllText(LogPath,
                    $"[Sender] {DateTime.Now}: ICE {state} — handlers desincritos\n");
            }
        };
    }

    // ── Receiver side ──────────────────────────────────────────────────────

    private void OnOfferReceived(string fromUserId, string offerSdp)
    {
        var webRtc = new WebRtcService();

        Action<string, string> iceCandReceivedHandler = async (from, cand) =>
        {
            if (from != fromUserId) return;
            try { await webRtc.AddIceCandidateAsync(cand); }
            catch (Exception ex)
            {
                File.AppendAllText(LogPath,
                    $"[ICE/Recv] {DateTime.Now}: {ex.GetType().Name}: {ex.Message}\n");
            }
        };
        _signaling.IceCandidateReceived += iceCandReceivedHandler;

        Dispatcher.Invoke(() =>
        {
            var result = MessageBox.Show(
                "Um amigo quer compartilhar a tela com você.\nAceitar?",
                "Compartilhamento recebido",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
            {
                _signaling.IceCandidateReceived -= iceCandReceivedHandler;
                _ = webRtc.DisposeAsync().AsTask();
                return;
            }
            _ = StartReceivingAsync(fromUserId, offerSdp, webRtc, iceCandReceivedHandler);
        });
    }

    private async Task StartReceivingAsync(
        string fromUserId,
        string offerSdp,
        WebRtcService webRtc,
        Action<string, string> iceCandReceivedHandler)
    {
        var vm = new ReceiveViewModel(webRtc, _signaling, fromUserId);
        vm.StopRequested += () =>
        {
            _signaling.IceCandidateReceived -= iceCandReceivedHandler;
            // Dispõe via ViewModel (que desinscreve seus próprios handlers corretamente)
            _ = vm.DisposeAsync().AsTask();
            Dispatcher.Invoke(ShowFriends);
        };

        var answerSdp = await webRtc.CreateAnswerAsync(offerSdp);
        await _signaling.SendAnswerAsync(fromUserId, answerSdp);

        Dispatcher.Invoke(() => MainContent.Content = new ReceiveView { DataContext = vm });
    }
}
