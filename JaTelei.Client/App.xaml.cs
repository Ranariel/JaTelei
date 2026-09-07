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
        Path.Combine(Path.GetTempPath(), "jatelei_error.txt");

    public static IConfiguration Config { get; private set; } = null!;

    static App()
    {
        // AppDomain handler registrado ANTES de qualquer outro código para capturar
        // TypeInitializationException e outros crashes pré-Dispatcher no log.
        AppDomain.CurrentDomain.UnhandledException += OnDomainException;
    }

    public App()
    {
        // ── Configuração ──────────────────────────────────────────────────────
        // IMPORTANTE: AddJsonStream() armazena a referência do stream e só lê
        // durante Build(). Por isso os streams NÃO podem estar em using blocks
        // que terminem antes de Build() — usamos MemoryStream para garantir que
        // os dados estejam em memória gerenciada e não sejam descartados cedo.
        var assembly = Assembly.GetExecutingAssembly();

        var builder = new ConfigurationBuilder();

        // appsettings.json (embutido na build)
        using (var raw = assembly.GetManifestResourceStream("JaTelei.Client.appsettings.json")!)
        {
            var ms = new MemoryStream();
            raw.CopyTo(ms);
            ms.Position = 0;
            builder.AddJsonStream(ms);
        }

        // appsettings.local.json (gerado pelo CI a partir dos secrets — gitignored)
        const string localResource = "JaTelei.Client.appsettings.local.json";
        if (assembly.GetManifestResourceNames().Contains(localResource))
        {
            using var raw = assembly.GetManifestResourceStream(localResource)!;
            var ms = new MemoryStream();
            raw.CopyTo(ms);
            ms.Position = 0;
            builder.AddJsonStream(ms);
        }

        Config = builder.Build();

        // ── Tratamento de exceções do Dispatcher e Tasks ───────────────────────
        DispatcherUnhandledException          += OnDispatcherException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTask;
    }

    private static void OnDomainException(object sender, UnhandledExceptionEventArgs e)
    {
        try
        {
            File.AppendAllText(LogPath,
                $"[Domain] {DateTime.Now}: isTerminating={e.IsTerminating}\n{e.ExceptionObject}\n\n");
        }
        catch { }
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
        e.SetObserved();

        // SocketException 995 = WSA_OPERATION_ABORTED — happens every time SIPSorcery's
        // pending UDP reads are cancelled by _pc.Close("dispose"). It is harmless noise;
        // log everything else.
        var inner = e.Exception?.InnerException;
        if (inner is System.Net.Sockets.SocketException se && se.ErrorCode == 995)
            return;

        try
        {
            File.AppendAllText(LogPath, $"[Task] {DateTime.Now}: {e.Exception}\n\n");
        }
        catch { }
    }
}
