using Avalonia;
using FastTorrentDownload.Services;

namespace FastTorrentDownload;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppLogger.LogException("Unhandled exception", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));
        using var instance = new Mutex(initiallyOwned: true, "Local\\WASD.FastTorrentDownload", out var created);
        if (!created)
            return;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        instance.ReleaseMutex();
    }

    private static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
