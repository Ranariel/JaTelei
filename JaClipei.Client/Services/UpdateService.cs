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

    /// <summary>
    /// Verifica se há versão mais nova. Retorna null se está atualizado ou falhou.
    /// </summary>
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
            return null; // sem internet ou servidor offline — ignora silenciosamente
        }
    }

    /// <summary>
    /// Baixa o novo exe e o substitui pelo atual via script bat, reiniciando o app.
    /// </summary>
    public static async Task DownloadAndRestartAsync(UpdateInfo update)
    {
        var currentExe = Process.GetCurrentProcess().MainModule!.FileName;
        var dir = Path.GetDirectoryName(currentExe)!;
        var newExe = Path.Combine(dir, "JaClipei-update.exe");
        var batPath = Path.Combine(Path.GetTempPath(), "jaclipei_update.bat");

        // Download
        using var response = await _http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        await using (var stream = await response.Content.ReadAsStreamAsync())
        await using (var file = File.Create(newExe))
            await stream.CopyToAsync(file);

        // Script: espera o app fechar, substitui o exe, reinicia
        var bat = $"""
            @echo off
            timeout /t 2 /nobreak >nul
            move /y "{newExe}" "{currentExe}"
            start "" "{currentExe}"
            del "%~f0"
            """;

        await File.WriteAllTextAsync(batPath, bat);

        Process.Start(new ProcessStartInfo(batPath) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
        Application.Current.Shutdown();
    }
}
