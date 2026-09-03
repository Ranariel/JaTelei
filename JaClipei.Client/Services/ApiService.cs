using System.Net.Http;
using System.Net.Http.Json;
using JaClipei.Client.Models;

namespace JaClipei.Client.Services;

public class ApiService
{
    private readonly HttpClient _http = new();
    public const string Base = "https://jaclipei.com/screenshare/api";

    public string? Token { get; private set; }
    public Guid? UserId { get; private set; }
    public string? Username { get; private set; }

    private void SetAuth() =>
        _http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token);

    public record LoginResult(string Token, string Username, Guid Id);
    public record RegisterResult(Guid Id, string Username);
    public record PendingRequest(Guid Id, string Username, DateTime CreatedAt);

    public async Task<LoginResult?> LoginAsync(string email, string password)
    {
        var res = await _http.PostAsJsonAsync($"{Base}/auth/login", new { email, password });
        if (!res.IsSuccessStatusCode) return null;
        var result = await res.Content.ReadFromJsonAsync<LoginResult>();
        Token = result?.Token;
        UserId = result?.Id;
        Username = result?.Username;
        SetAuth();
        return result;
    }

    public async Task<RegisterResult?> RegisterAsync(string username, string email, string password)
    {
        var res = await _http.PostAsJsonAsync($"{Base}/auth/register", new { username, email, password });
        if (!res.IsSuccessStatusCode) return null;
        return await res.Content.ReadFromJsonAsync<RegisterResult>();
    }

    public async Task<List<Friend>> GetFriendsAsync()
        => await _http.GetFromJsonAsync<List<Friend>>($"{Base}/friends") ?? [];

    public async Task<bool> SendFriendRequestAsync(string username)
        => (await _http.PostAsync($"{Base}/friends/request/{username}", null)).IsSuccessStatusCode;

    public async Task<List<PendingRequest>> GetPendingAsync()
        => await _http.GetFromJsonAsync<List<PendingRequest>>($"{Base}/friends/pending") ?? [];

    public async Task<bool> AcceptFriendAsync(Guid friendshipId)
        => (await _http.PostAsync($"{Base}/friends/accept/{friendshipId}", null)).IsSuccessStatusCode;
}
