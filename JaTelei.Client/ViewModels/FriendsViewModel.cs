using System.Collections.ObjectModel;
using System.Windows;
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

    public string SessionCode { get; } = $"JT-{Random.Shared.Next(1000, 9999)}";
    public string SessionLink => $"https://jatelei.com/sala/{SessionCode}";
    public string FrameRateLabel => "60 fps";
    public string LatencyLabel => IsSharing ? "Latencia 42 ms" : "Aguardando";
    public string UploadLabel => IsSharing ? "12.8 Mbps" : "0 Mbps";
    public string SharingStatus => IsSharing ? (IsPaused ? "Pausado" : "Compartilhando tela") : "Pronto para compartilhar";
    public string ViewerCountText => IsSharing ? "1 conectado" : "0 conectado";
    public string ViewerStatus => IsSharing ? "Visualizador conectado" : "Nenhum visualizador";
    public string ViewerDetail => SelectedFriend is null
        ? "Escolha um contato para iniciar"
        : IsSharing
            ? $"{SelectedFriend.Username} esta vendo sua tela"
            : $"Enviar convite para {SelectedFriend.Username}";
    public string PrimaryActionText => IsSharing ? "Compartilhando" : "Compartilhar";
    public string PauseActionText => IsPaused ? "Retomar" : "Pausar";
    public string PreviewHint => IsSharing
        ? "Sua tela esta sendo compartilhada com outro usuario pela internet."
        : "Escolha um contato e inicie o compartilhamento.";

    /// <summary>Inverse of IsSharing - used in XAML visibility bindings.</summary>
    public bool IsNotSharing => !IsSharing;

    partial void OnIsSharingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsNotSharing));
        OnPropertyChanged(nameof(SharingStatus));
        OnPropertyChanged(nameof(ViewerCountText));
        OnPropertyChanged(nameof(ViewerStatus));
        OnPropertyChanged(nameof(ViewerDetail));
        OnPropertyChanged(nameof(PrimaryActionText));
        OnPropertyChanged(nameof(PreviewHint));
        OnPropertyChanged(nameof(LatencyLabel));
        OnPropertyChanged(nameof(UploadLabel));
    }

    partial void OnIsPausedChanged(bool value)
    {
        OnPropertyChanged(nameof(SharingStatus));
        OnPropertyChanged(nameof(PauseActionText));
    }

    partial void OnSelectedFriendChanged(Friend? value)
    {
        OnPropertyChanged(nameof(ViewerDetail));
    }

    public event Action<Friend>? StartShareRequested;
    public event Action?         StopShareRequested;

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
        if (SelectedFriend is null) return;
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

    [RelayCommand]
    private void CopyInvite()
    {
        try
        {
            Clipboard.SetText(SessionLink);
            StatusMessage = "Convite copiado.";
        }
        catch
        {
            StatusMessage = SessionLink;
        }
    }

    public void OnSharingStarted()
    {
        IsPaused = false;
        IsSharing = true;
    }

    public void OnSharingStopped()
    {
        IsSharing = false;
        IsPaused = false;
        SelfPreviewImage = null;
    }

    public void OnPreviewFrame(BitmapSource img) => SelfPreviewImage = img;
}
