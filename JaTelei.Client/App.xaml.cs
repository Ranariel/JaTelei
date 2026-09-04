using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Configuration;

namespace JaTelei.Client;

public partial class App : Application
{
    private static readonly string LogPath =
        Path.Combine(Path.GetTempPath(), "jaclipei_error.txt");

    public static IConfiguration Config { get; private set; } = null!;

    public App()
    {
        // ── Configuração ──────────────────────────────────────────────────────
        // Carrega appsettings.json (embutido na build) e, se presente,
        // appsettings.local.json (gerado pelo CI a partir de secrets — gitignored).
        var assembly = Assembly.GetExecutingAssembly();
        using var defaultStream =
            assembly.GetManifestResourceStream("JaTelei.Client.appsettings.json")!;

        var builder = new ConfigurationBuilder().AddJsonStream(defaultStream);

        const string localResource = "JaTelei.Client.appsettings.local.json";
        if (assembly.GetManifestResourceNames().Contains(localResource))
        {
            using var localStream = assembly.GetManifestResourceStream(localResource)!;
            builder.AddJsonStream(localStream);
        }

        Config = builder.Build();

        // ── Tratamento de exceções globais ────────────────────────────────────
        DispatcherUnhandledException += OnDispatcherException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTask;
    }

    private static void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        File.AppendAllText(LogPath, $"[UI] {DateTime.Now}: {e.Exception}\n\n");
        e.Handled = true; // evita crash, continua rodando
    }

    private static void OnUnobservedTask(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        File.AppendAllText(LogPath, $"[Task] {DateTime.Now}: {e.Exception}\n\n");
        e.SetObserved();
    }
}
