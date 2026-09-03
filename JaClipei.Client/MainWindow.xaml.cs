using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using JaClipei.Client.Services;
using JaClipei.Client.ViewModels;
using JaClipei.Client.Views;
using JaClipei.Client.Models;

namespace JaClipei.Client;

public partial class MainWindow : Window
{
    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "jaclipei_error.txt");
    private readonly ApiService _api = new();
    private readonly SignalingService _signaling = new();
    private UpdateService.UpdateInfo? _pendingUpdate;

    public MainWindow()
    {
        InitializeComponent();
        Log("[MainWindow] InitializeComponent OK");
        ShowLogin();
        _ = CheckForUpdateAsync();
    }

    private static void Log(string msg)
        => File.AppendAllText(LogPath, $"{msg} @ {DateTime.Now:HH:mm:ss}\n");

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
            Log("[ShowLogin] criando vm");
            var vm = new LoginViewModel(_api);
            Log("[ShowLogin] vm OK");

            vm.LoginSuccess += async () =>
            {
                try
                {
                    await _signaling.ConnectAsync(_api.Token!);
                    _signaling.OfferReceived += OnOfferReceived;
                }
                catch (Exception ex)
                {
                    Log($"[SignalR] {ex}");
                }
                Dispatcher.Invoke(ShowFriends);
            };

            Log("[ShowLogin] criando view");
            var view = new LoginView { DataContext = vm };
            Log("[ShowLogin] view OK, atribuindo content");
            MainContent.Content = view;
            Log("[ShowLogin] content atribuido");
        }
        catch (Exception ex)
        {
            Log($"[ShowLogin ERRO] {ex}");
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
        try
        {
            var vm = new FriendsViewModel(_api, _signaling);
            vm.StartShareRequested += friend => _ = StartSendingAsync(friend);
            _ = vm.LoadCommand.ExecuteAsync(null);
            MainContent.Content = new FriendsView { DataContext = vm };
        }
        catch (Exception ex)
        {
            Log($"[ShowFriends ERRO] {ex}");
            MainContent.Content = new TextBlock
            {
                Text = $"ERRO FRIENDS: {ex.GetType().Name}\n{ex.Message}",
                Foreground = new SolidColorBrush(Colors.Red),
                FontSize = 16,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(20)
            };
        }
    }

    // ── Sender side ────────────────────────────────────────────────────────

    private async Task StartSendingAsync(Friend friend)
    {
        var webRtc = new WebRtcService();

        webRtc.IceCandidateReady += async c =>
            await _signaling.SendIceCandidateAsync(friend.Id.ToString(), c);

        _signaling.AnswerReceived += async (from, sdp) =>
        {
            if (from != friend.Id.ToString()) return;
            await webRtc.SetRemoteAnswerAsync(sdp);
        };

        _signaling.IceCandidateReceived += async (from, cand) =>
        {
            if (from != friend.Id.ToString()) return;
            await webRtc.AddIceCandidateAsync(cand);
        };

        var offerSdp = await webRtc.CreateOfferAsync();
        await _signaling.SendOfferAsync(friend.Id.ToString(), offerSdp);

        for (int i = 0; i < 150 && !webRtc.IsDataChannelOpen; i++)
            await Task.Delay(100);

        if (webRtc.IsDataChannelOpen)
            webRtc.StartCapture(fps: 15);
        else
            MessageBox.Show("Não foi possível conectar. Verifique se o amigo aceitou.", "Erro");
    }

    // ── Receiver side ──────────────────────────────────────────────────────

    private void OnOfferReceived(string fromUserId, string offerSdp)
    {
        Dispatcher.Invoke(() =>
        {
            var result = MessageBox.Show(
                "Um amigo quer compartilhar a tela com você.\nAceitar?",
                "Compartilhamento recebido",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes) return;
            _ = StartReceivingAsync(fromUserId, offerSdp);
        });
    }

    private async Task StartReceivingAsync(string fromUserId, string offerSdp)
    {
        var webRtc = new WebRtcService();

        _signaling.IceCandidateReceived += async (from, cand) =>
        {
            if (from != fromUserId) return;
            await webRtc.AddIceCandidateAsync(cand);
        };

        var vm = new ReceiveViewModel(webRtc, _signaling, fromUserId);
        vm.StopRequested += () =>
        {
            _ = webRtc.DisposeAsync().AsTask();
            Dispatcher.Invoke(ShowFriends);
        };

        var answerSdp = await webRtc.CreateAnswerAsync(offerSdp);
        await _signaling.SendAnswerAsync(fromUserId, answerSdp);

        Dispatcher.Invoke(() => MainContent.Content = new ReceiveView { DataContext = vm });
    }

    // ── Barra de título ────────────────────────────────────────────────────

    private void Minimize_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object s, RoutedEventArgs e) => Close();
}
