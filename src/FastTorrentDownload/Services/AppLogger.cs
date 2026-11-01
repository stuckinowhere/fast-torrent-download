namespace FastTorrentDownload.Services;

public static class AppLogger
{
    private const long MaxLogBytes = 5 * 1024 * 1024;
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
                RotateIfNeeded(file);
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

    // Keeps one previous generation so a long-running install never fills the disk.
    private static void RotateIfNeeded(string file)
    {
        var info = new FileInfo(file);
        if (!info.Exists || info.Length < MaxLogBytes)
        {
            return;
        }

        File.Move(file, file + ".1", overwrite: true);
    }
}
