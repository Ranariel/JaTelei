using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JaTelei.Client.Models;
using JaTelei.Client.Services;

namespace JaTelei.Client.ViewModels;

public partial class FriendsViewModel(ApiService api, SignalingService signaling) : ObservableObject
{
    public ObservableCollection<Friend> Friends { get; } = [];
    public ObservableCollection<ApiService.PendingRequest> PendingRequests { get; } = [];

    [ObservableProperty] private string _addUsername = string.Empty;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private Friend? _selectedFriend;

    public event Action<Friend>? StartShareRequested;

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
}
