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
    [ObservableProperty] private BitmapSource? _selfPreviewImage;

    /// <summary>Inverse of IsSharing — used in XAML visibility bindings.</summary>
    public bool IsNotSharing => !IsSharing;

    partial void OnIsSharingChanged(bool value) => OnPropertyChanged(nameof(IsNotSharing));

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
        StatusMessage = ok ? $"Pedido enviado para {AddUsername}." : "Usuário não encontrado ou pedido já existe.";
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

    public void OnSharingStarted()  => IsSharing = true;
    public void OnSharingStopped()  { IsSharing = false; SelfPreviewImage = null; }
    public void OnPreviewFrame(BitmapSource img) => SelfPreviewImage = img;
}
