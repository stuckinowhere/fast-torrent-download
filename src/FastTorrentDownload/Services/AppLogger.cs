namespace FastTorrentDownload.Services;

public static class AppLogger
{
    private static readonly object Sync = new();
    private static string? _logFile;

    public static void Init(AppPaths paths)
    {
        try
        {
            Directory.CreateDirectory(paths.LogsDirectory);
            _logFile = Path.Combine(paths.LogsDirectory, "app.log");
            Log("=== app started ===");
        }
        catch
        {
            // Logging must never break the app.
        }
    }

    public static void Log(string message)
    {
        var file = _logFile;
        if (file is null)
        {
            return;
        }

        try
        {
            lock (Sync)
            {
                File.AppendAllText(file, $"[{DateTime.UtcNow:O}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never break the app.
        }
    }

    public static void LogException(string context, Exception exception) =>
        Log($"{context}: {exception}");
}
