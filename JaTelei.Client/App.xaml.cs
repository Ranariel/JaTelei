using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace JaTelei.Client;

public partial class App : Application
{
    private static readonly string LogPath =
        Path.Combine(Path.GetTempPath(), "jaclipei_error.txt");

    public App()
    {
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
