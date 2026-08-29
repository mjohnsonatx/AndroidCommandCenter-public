using Avalonia;
using System;
using System.IO;

namespace AndroidCommandCenter;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Headless CI mode — lets systemd timers run the pipeline with no GUI.
        if (args.Length > 0 && args[0] is "ci" or "rollback")
            return Cli.CliEntry.RunAsync(args).GetAwaiter().GetResult();

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            // Last-resort crash capture. A silent exit on Wayland/X11 startup
            // failure is otherwise impossible to diagnose.
            var crash = Path.Combine(Paths.StateDir, "crash.log");
            try
            {
                Directory.CreateDirectory(Paths.StateDir);
                File.AppendAllText(crash,
                    $"\n=== {DateTime.Now:O} ===\n{ex}\n");
            }
            catch { }
            Console.Error.WriteLine($"FATAL: {ex.Message}\nLogged to {crash}");
            return 70;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}