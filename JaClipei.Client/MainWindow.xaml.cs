using System.Windows;
using JaClipei.Client.Services;
using JaClipei.Client.ViewModels;
using JaClipei.Client.Views;
using JaClipei.Client.Models;

namespace JaClipei.Client;

public partial class MainWindow : Window
{
    private readonly ApiService _api = new();
    private readonly SignalingService _signaling = new();

    public MainWindow()
    {
        InitializeComponent();
        ShowLogin();
    }

    // ── Login ──────────────────────────────────────────────────────────────

    private void ShowLogin()
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
                // SignalR falhou mas não bloqueia o app — funcionalidades offline ainda funcionam
                System.Diagnostics.Debug.WriteLine($"SignalR error: {ex.Message}");
            }
            ShowFriends();
        };
        MainContent.Content = new LoginView { DataContext = vm };
    }

    // ── Friends ────────────────────────────────────────────────────────────

    private void ShowFriends()
    {
        var vm = new FriendsViewModel(_api, _signaling);
        vm.StartShareRequested += friend => _ = StartSendingAsync(friend);
        _ = vm.LoadCommand.ExecuteAsync(null);
        MainContent.Content = new FriendsView { DataContext = vm };
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
            MessageBox.Show("Não foi possível conectar com o amigo. Verifique se ele aceitou.", "Erro");
    }

    // ── Receiver side ──────────────────────────────────────────────────────

    private void OnOfferReceived(string fromUserId, string offerSdp)
    {
        Dispatcher.Invoke(() =>
        {
            var result = MessageBox.Show(
                $"Um amigo quer compartilhar a tela com você.\nAceitar?",
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
