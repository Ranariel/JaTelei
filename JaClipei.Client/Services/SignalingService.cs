using Microsoft.AspNetCore.SignalR.Client;

namespace JaClipei.Client.Services;

public class SignalingService
{
    private HubConnection? _hub;
    private const string HubUrl = "https://jaclipei.com/screenshare/hubs/signaling";

    public event Action<string, string>? OfferReceived;
    public event Action<string, string>? AnswerReceived;
    public event Action<string, string>? IceCandidateReceived;
    public event Action<string>? ErrorReceived;

    public async Task ConnectAsync(string token)
    {
        _hub = new HubConnectionBuilder()
            .WithUrl($"{HubUrl}?access_token={token}")
            .WithAutomaticReconnect()
            .Build();

        _hub.On<string, string>("ReceiveOffer",        (from, sdp)  => OfferReceived?.Invoke(from, sdp));
        _hub.On<string, string>("ReceiveAnswer",       (from, sdp)  => AnswerReceived?.Invoke(from, sdp));
        _hub.On<string, string>("ReceiveIceCandidate", (from, cand) => IceCandidateReceived?.Invoke(from, cand));
        _hub.On<string>("Error",                       msg          => ErrorReceived?.Invoke(msg));

        await _hub.StartAsync();
    }

    public Task SendOfferAsync(string targetUserId, string sdp)       => _hub!.InvokeAsync("SendOffer",        targetUserId, sdp);
    public Task SendAnswerAsync(string targetUserId, string sdp)      => _hub!.InvokeAsync("SendAnswer",       targetUserId, sdp);
    public Task SendIceCandidateAsync(string targetUserId, string c)  => _hub!.InvokeAsync("SendIceCandidate", targetUserId, c);

    public async Task DisconnectAsync()
    {
        if (_hub is not null) await _hub.DisposeAsync();
    }
}
