using Avalonia;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace AzIPTV;

class Program
{
    public const string AppVersion = "v1.1.1c";
    public const string AppName = "AzIPTV";
    public static string AppDisplayName => $"{AppName} {AppVersion}";

    [STAThread]
    public static void Main(string[] args)
    {
        AppLogger.Init();

        // Catch any unhandled exception on any thread before the app crashes.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppLogger.Log($"FATAL UnhandledException: {e.ExceptionObject}");

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLogger.Log($"FATAL UnobservedTask: {e.Exception}");
            e.SetObserved();
        };

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
