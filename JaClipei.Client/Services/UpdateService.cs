using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;

namespace JaClipei.Client.Services;

public class UpdateService
{
    private static readonly HttpClient _http = new();
    private const string CheckUrl = "https://jaclipei.com/screenshare/api/update/latest";

    public record UpdateInfo(string Version, string DownloadUrl, string Filename);

    public static async Task<UpdateInfo?> CheckAsync(string currentVersion)
    {
        try
        {
            var info = await _http.GetFromJsonAsync<UpdateInfo>(CheckUrl);
            if (info is null) return null;
            return string.Compare(info.Version, currentVersion, StringComparison.OrdinalIgnoreCase) > 0
                ? info
                : null;
        }
        catch
        {
            return null;
        }
    }

    public static async Task DownloadAndRestartAsync(UpdateInfo update)
    {
        // Baixa o instalador para %TEMP% (sem acentos no caminho)
        var tempDir  = Path.GetTempPath();
        var setupExe = Path.Combine(tempDir, "JaClipeiSetup-update.exe");

        using var response = await _http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        await using (var stream = await response.Content.ReadAsStreamAsync())
        await using (var file   = File.Create(setupExe))
            await stream.CopyToAsync(file);

        // Roda o instalador silencioso — ele fecha o app via taskkill e instala o novo exe
        Process.Start(new ProcessStartInfo(setupExe)
        {
            Arguments       = "/VERYSILENT /NORESTART /CLOSEAPPLICATIONS",
            UseShellExecute = true
        });

        Application.Current.Shutdown();
    }
}
