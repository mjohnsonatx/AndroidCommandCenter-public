using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using System;
using System.IO;
using Avalonia.Controls;

namespace AndroidCommandCenter;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Unobserved task exceptions in async void handlers would otherwise
        // vanish. Surface them instead of losing a failed deploy silently.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogFatal("UnobservedTaskException", e.Exception);
            e.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LogFatal("UnhandledException", e.ExceptionObject as Exception);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static void LogFatal(string kind, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Paths.StateDir);
            File.AppendAllText(Path.Combine(Paths.StateDir, "crash.log"),
                $"\n=== {DateTime.Now:O} {kind} ===\n{ex}\n");
        }
        catch { }
    }
}

