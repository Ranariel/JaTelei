using System.Collections.ObjectModel;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JaTelei.Client.Models;
using JaTelei.Client.Services;

namespace JaTelei.Client.ViewModels;

public partial class FriendsViewModel(ApiService api, SignalingService _) : ObservableObject
{
    public ObservableCollection<Friend> Friends { get; } = [];
    public ObservableCollection<ApiService.PendingRequest> PendingRequests { get; } = [];

    [ObservableProperty] private string _addUsername = string.Empty;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private Friend? _selectedFriend;
    [ObservableProperty] private bool _isSharing;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private bool _includeSystemAudio = true;
    [ObservableProperty] private BitmapSource? _selfPreviewImage;
    [ObservableProperty] private string _activeSection = "share";
    [ObservableProperty] private int _selectedResolutionHeight = 720;
    [ObservableProperty] private int _selectedFps = 30;
    [ObservableProperty] private int _currentUploadKbps;
    [ObservableProperty] private int _currentLatencyMs;
    [ObservableProperty] private int _packetLossPermille;
    [ObservableProperty] private int _encoderBitrateKbps;
    [ObservableProperty] private bool _isViewerConnected;
    [ObservableProperty] private string _sharingTargetName = string.Empty;

    public string QualityLabel => FormatResolution(SelectedResolutionHeight);
    public string FrameRateLabel => $"{SelectedFps} fps";
    public string LatencyLabel => IsSharing ? (CurrentLatencyMs > 0 ? $"Atraso {CurrentLatencyMs} ms" : "Medindo") : "Aguardando";
    public string UploadLabel => IsSharing ? $"Atual {CurrentUploadKbps} kbps" : "Atual 0 kbps";
    public string TargetBitrateLabel => $"{GetRecommendedBitrateKbps(SelectedResolutionHeight, SelectedFps)} kbps";
    public string PacketLossLabel => IsSharing ? $"Perda {PacketLossPermille / 10.0:F1}%" : "Perda 0.0%";
    public string EncoderBitrateLabel => IsSharing && EncoderBitrateKbps > 0 ? $"Encoder {EncoderBitrateKbps} kbps" : $"Encoder {GetRecommendedBitrateKbps(SelectedResolutionHeight, SelectedFps)} kbps";
    public string SharingStatus => IsSharing ? (IsPaused ? "Pausado" : "Compartilhando tela") : "Pronto para compartilhar";
    public string ViewerCountText => IsViewerConnected ? "1 conectado" : "0 conectado";
    public string ViewerStatus => IsViewerConnected ? "Visualizador conectado" : "Nenhum visualizador";
    public string ViewerDetail => SelectedFriend is null
        ? "Escolha um contato para iniciar"
        : IsViewerConnected
            ? $"{SelectedFriend.Username} esta vendo sua tela"
            : $"Enviar convite para {SelectedFriend.Username}";
    public string ShareActionText => IsSharing ? "Trocar tela" : "Escolher e compartilhar";
    public string PauseActionText => IsPaused ? "Retomar" : "Pausar";
    public string PreviewHint => IsSharing
        ? string.IsNullOrEmpty(SharingTargetName)
            ? "Sua tela esta sendo compartilhada com outro usuario pela internet."
            : $"Compartilhando \"{SharingTargetName}\" pela internet."
        : "Escolha o destino e a tela que deseja transmitir.";

    public bool IsNotSharing => !IsSharing;
    public bool IsShareSection => ActiveSection == "share";
    public bool IsDevicesSection => ActiveSection == "devices";
    public bool IsSettingsSection => ActiveSection == "settings";

    partial void OnIsSharingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsNotSharing));
        OnPropertyChanged(nameof(SharingStatus));
        OnPropertyChanged(nameof(ViewerCountText));
        OnPropertyChanged(nameof(ViewerStatus));
        OnPropertyChanged(nameof(ViewerDetail));
        OnPropertyChanged(nameof(ShareActionText));
        OnPropertyChanged(nameof(PreviewHint));
        OnPropertyChanged(nameof(LatencyLabel));
        OnPropertyChanged(nameof(UploadLabel));
    }

    partial void OnIsPausedChanged(bool value)
    {
        OnPropertyChanged(nameof(SharingStatus));
        OnPropertyChanged(nameof(PauseActionText));
        PauseChanged?.Invoke(value);
    }

    partial void OnIsViewerConnectedChanged(bool value)
    {
        OnPropertyChanged(nameof(ViewerCountText));
        OnPropertyChanged(nameof(ViewerStatus));
        OnPropertyChanged(nameof(ViewerDetail));
    }

    partial void OnSharingTargetNameChanged(string value)
    {
        OnPropertyChanged(nameof(PreviewHint));
    }

    partial void OnSelectedFriendChanged(Friend? value)
    {
        OnPropertyChanged(nameof(ViewerDetail));
    }

    partial void OnActiveSectionChanged(string value)
    {
        OnPropertyChanged(nameof(IsShareSection));
        OnPropertyChanged(nameof(IsDevicesSection));
        OnPropertyChanged(nameof(IsSettingsSection));
    }

    partial void OnSelectedResolutionHeightChanged(int value)
    {
        OnPropertyChanged(nameof(QualityLabel));
        OnPropertyChanged(nameof(UploadLabel));
        OnPropertyChanged(nameof(TargetBitrateLabel));
    }

    partial void OnSelectedFpsChanged(int value)
    {
        OnPropertyChanged(nameof(FrameRateLabel));
        OnPropertyChanged(nameof(TargetBitrateLabel));
        OnPropertyChanged(nameof(EncoderBitrateLabel));
    }

    partial void OnCurrentUploadKbpsChanged(int value)
    {
        OnPropertyChanged(nameof(UploadLabel));
    }

    partial void OnCurrentLatencyMsChanged(int value)
    {
        OnPropertyChanged(nameof(LatencyLabel));
    }

    partial void OnPacketLossPermilleChanged(int value)
    {
        OnPropertyChanged(nameof(PacketLossLabel));
    }

    partial void OnEncoderBitrateKbpsChanged(int value)
    {
        OnPropertyChanged(nameof(EncoderBitrateLabel));
    }

    public event Action<Friend>? StartShareRequested;
    public event Action?         StopShareRequested;
    public event Action?         LogoutRequested;
    /// <summary>Fired whenever IsPaused changes; passes the new paused state.</summary>
    public event Action<bool>?   PauseChanged;

    [RelayCommand]
    public async Task LoadAsync()
    {
        Friends.Clear();
        foreach (var f in await api.GetFriendsAsync()) Friends.Add(f);

        PendingRequests.Clear();
        foreach (var p in await api.GetPendingAsync()) PendingRequests.Add(p);
    }

    [RelayCommand]
    private async Task AddFriend()
    {
        if (string.IsNullOrWhiteSpace(AddUsername)) return;
        bool ok = await api.SendFriendRequestAsync(AddUsername.Trim());
        StatusMessage = ok ? $"Pedido enviado para {AddUsername}." : "Usuario nao encontrado ou pedido ja existe.";
        AddUsername = string.Empty;
    }

    [RelayCommand]
    private async Task AcceptRequest(ApiService.PendingRequest req)
    {
        await api.AcceptFriendAsync(req.Id);
        await LoadAsync();
    }

    [RelayCommand]
    private void ShareScreen()
    {
        if (SelectedFriend is null)
        {
            StatusMessage = "Selecione um contato antes de compartilhar.";
            return;
        }
        StartShareRequested?.Invoke(SelectedFriend);
    }

    [RelayCommand]
    private void StopShare() => StopShareRequested?.Invoke();

    [RelayCommand]
    private void TogglePause()
    {
        if (!IsSharing) return;
        IsPaused = !IsPaused;
    }

    [RelayCommand] private void ShowShare() => ActiveSection = "share";
    [RelayCommand] private void ShowDevices() => ActiveSection = "devices";
    [RelayCommand] private void ShowSettings() => ActiveSection = "settings";
    [RelayCommand] private void Logout() => LogoutRequested?.Invoke();

    public void SetViewerConnected(bool connected)
    {
        IsViewerConnected = connected;
    }

    public void OnSharingStarted(ShareTarget target)
    {
        SelectedResolutionHeight = target.ResolutionHeight;
        SelectedFps = target.Fps;
        CurrentUploadKbps = 0;
        CurrentLatencyMs = 0;
        PacketLossPermille = 0;
        EncoderBitrateKbps = GetRecommendedBitrateKbps(target.ResolutionHeight, target.Fps);
        IsPaused = false;
        IsViewerConnected = false;
        SharingTargetName = target.DisplayName ?? string.Empty;
        IsSharing = true;
    }

    public void OnSharingStopped()
    {
        IsSharing = false;
        IsPaused = false;
        IsViewerConnected = false;
        SharingTargetName = string.Empty;
        CurrentUploadKbps = 0;
        CurrentLatencyMs = 0;
        PacketLossPermille = 0;
        EncoderBitrateKbps = 0;
        SelfPreviewImage = null;
    }

    public void OnPreviewFrame(BitmapSource img) => SelfPreviewImage = img;

    public void OnNetworkStats(WebRtcService.NetworkStats stats)
    {
        CurrentUploadKbps = stats.UploadKbps;
        CurrentLatencyMs = stats.PipelineDelayMs;
        PacketLossPermille = stats.LossPermille;
        EncoderBitrateKbps = stats.EncoderBitrateKbps;
    }

    private static string FormatResolution(int height) => height switch
    {
        0 => "Nativa",
        1080 => "1080p",
        720 => "720p",
        480 => "480p",
        360 => "360p",
        240 => "240p",
        160 => "160p",
        _ => $"{height}p"
    };

    // Bitrate targets for H.264 screen-sharing (CBR ceiling passed to encoder).
    // 60fps variants are ~50% higher than 30fps because twice the frames need encoding.
    public static int GetRecommendedBitrateKbps(int height, int fps = 30)
    {
        bool hi = fps >= 60;
        return height switch
        {
            <= 0    => hi ? 12_000 : 8_000,   // Native
            >= 1080 => hi ? 12_000 : 8_000,   // 1080p
            >= 720  => hi ?  8_000 : 5_000,   // 720p
            >= 480  => hi ?  4_000 : 2_500,   // 480p
            >= 360  => hi ?  2_500 : 1_500,   // 360p
            >= 240  => hi ?  1_200 :   800,   // 240p
            _       => hi ?    800 :   500,   // 160p
        };
    }
}
