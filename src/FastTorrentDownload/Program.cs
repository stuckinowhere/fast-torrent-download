using Avalonia;

namespace FastTorrentDownload;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        using var instance = new Mutex(initiallyOwned: true, "Local\\WASD.FastTorrentDownload", out var created);
        if (!created)
            return;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        instance.ReleaseMutex();
    }

    private static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
