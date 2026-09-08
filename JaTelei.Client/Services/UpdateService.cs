using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Windows;

namespace JaTelei.Client.Services;

public class UpdateService
{
    private static readonly HttpClient _http = new();
    private static string CheckUrl =>
        App.Config["App:UpdateCheckUrl"] ?? "https://jaclipei.com/screenshare/api/update/latest";

    // sha256 é opcional — releases antigas (antes de v1.0.149) não têm hash
    public record UpdateInfo(string Version, string DownloadUrl, string Filename, string? Sha256 = null);

    public static async Task<UpdateInfo?> CheckAsync(string currentVersion)
    {
        try
        {
            var info = await _http.GetFromJsonAsync<UpdateInfo>(CheckUrl);
            if (info is null) return null;

            // Usa Version.Parse para comparação numérica correta.
            // string.Compare é lexicográfico e falha com versões de 3+ dígitos:
            // ex: "1.0.99" > "1.0.100" como string, mas 1.0.99 < 1.0.100 numericamente.
            if (!Version.TryParse(info.Version, out var remoteVer)) return null;
            if (!Version.TryParse(currentVersion, out var localVer)) return null;

            return remoteVer > localVer ? info : null;
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
        var setupExe = Path.Combine(tempDir, "JaTeleiSetup-update.exe");

        using var response = await _http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        await using (var stream = await response.Content.ReadAsStreamAsync())
        await using (var file   = File.Create(setupExe))
            await stream.CopyToAsync(file);

        // ── Verificar SHA256 antes de executar ────────────────────────────────
        // Se o servidor não enviou hash (releases antigas), ignora a verificação.
        // Se enviou e o hash não bater, rejeita o instalador.
        if (!string.IsNullOrWhiteSpace(update.Sha256))
        {
            var actual = await ComputeSha256Async(setupExe);
            if (!string.Equals(actual, update.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(setupExe);
                throw new InvalidOperationException(
                    $"SHA256 inválido no instalador baixado.\nEsperado: {update.Sha256}\nObtido:   {actual}");
            }
        }

        // Roda o instalador silencioso — ele fecha o app via taskkill e instala o novo exe
        Process.Start(new ProcessStartInfo(setupExe)
        {
            Arguments       = "/VERYSILENT /NORESTART /CLOSEAPPLICATIONS",
            UseShellExecute = true
        });

        Application.Current.Shutdown();
    }

    private static async Task<string> ComputeSha256Async(string filePath)
    {
        using var sha = SHA256.Create();
        await using var stream = File.OpenRead(filePath);
        var hash = await sha.ComputeHashAsync(stream);
        return Convert.ToHexString(hash).ToLower();
    }
}
