using System.Net.Http;
using System.Net.Http.Json;
using JaTelei.Client.Models;

namespace JaTelei.Client.Services;

public class ApiService
{
    private readonly HttpClient _http = new();
    public static string Base =>
        App.Config["App:ApiBaseUrl"] ?? "https://jaclipei.com/screenshare/api";

    public string? Token { get; private set; }
    public Guid? UserId { get; private set; }
    public string? Username { get; private set; }

    private void SetAuth() =>
        _http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token);

    public record LoginResult(string Token, string Username, Guid Id);
    public record RegisterResult(Guid Id, string Username);
    public record PendingRequest(Guid Id, string Username, DateTime CreatedAt);

    public async Task<(LoginResult? Result, string? Error)> LoginAsync(string username, string password)
    {
        var res = await _http.PostAsJsonAsync($"{Base}/auth/login", new { username, password });
        if (!res.IsSuccessStatusCode)
        {
            var err = await TryReadError(res);
            return (null, err ?? "Apelido ou senha incorretos.");
        }
        var result = await res.Content.ReadFromJsonAsync<LoginResult>();
        Token = result?.Token;
        UserId = result?.Id;
        Username = result?.Username;
        SetAuth();
        return (result, null);
    }

    public async Task<(RegisterResult? Result, string? Error)> RegisterAsync(string username, string? email, string password)
    {
        var res = await _http.PostAsJsonAsync($"{Base}/auth/register", new { username, email, password });
        if (!res.IsSuccessStatusCode)
        {
            var err = await TryReadError(res);
            return (null, err ?? "Erro ao criar conta.");
        }
        var result = await res.Content.ReadFromJsonAsync<RegisterResult>();
        return (result, null);
    }

    private static async Task<string?> TryReadError(HttpResponseMessage res)
    {
        try
        {
            var obj = await res.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            if (obj.TryGetProperty("error", out var err)) return err.GetString();
        }
        catch { }
        return null;
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
