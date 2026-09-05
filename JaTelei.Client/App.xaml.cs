using System;
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

    static App()
    {
        // Register AppDomain handler BEFORE any other code runs so that
        // TypeInitializationException (e.g. from FFmpeg static constructor)
        // is caught and written to the log even if the WPF Dispatcher hasn't
        // started yet.
        AppDomain.CurrentDomain.UnhandledException += OnDomainException;
    }

    public App()
    {
        // ── Configuração ──────────────────────────────────────────────────────
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

        // ── Tratamento de exceções do Dispatcher e Tasks ───────────────────────
        DispatcherUnhandledException    += OnDispatcherException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTask;
    }

    private static void OnDomainException(object sender, UnhandledExceptionEventArgs e)
    {
        try
        {
            File.AppendAllText(LogPath,
                $"[Domain] {DateTime.Now}: isTerminating={e.IsTerminating}\n{e.ExceptionObject}\n\n");
        }
        catch { /* last-resort: ignore IO errors in the crash handler */ }
    }

    private static void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            File.AppendAllText(LogPath, $"[UI] {DateTime.Now}: {e.Exception}\n\n");
        }
        catch { }
        e.Handled = true;
    }

    private static void OnUnobservedTask(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        try
        {
            File.AppendAllText(LogPath, $"[Task] {DateTime.Now}: {e.Exception}\n\n");
        }
        catch { }
        e.SetObserved();
    }
}
