using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
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
        var currentExe = Process.GetCurrentProcess().MainModule!.FileName;

        // Baixa para %TEMP% para evitar problemas com acentos no caminho
        var tempDir = Path.GetTempPath();
        var newExe = Path.Combine(tempDir, "JaClipei-update.exe");
        var batPath = Path.Combine(tempDir, "jaclipei_update.bat");

        // Download
        using var response = await _http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        await using (var stream = await response.Content.ReadAsStreamAsync())
        await using (var file = File.Create(newExe))
            await stream.CopyToAsync(file);

        // Bat escrito com encoding ANSI (Windows-1252) para cmd.exe ler corretamente
        var bat = "@echo off\r\n"
                + "timeout /t 2 /nobreak >nul\r\n"
                + $"move /y \"{newExe}\" \"{currentExe}\"\r\n"
                + $"start \"\" \"{currentExe}\"\r\n"
                + "del \"%~f0\"\r\n";

        await File.WriteAllTextAsync(batPath, bat, Encoding.Default);

        Process.Start(new ProcessStartInfo(batPath)
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
        Application.Current.Shutdown();
    }
}
