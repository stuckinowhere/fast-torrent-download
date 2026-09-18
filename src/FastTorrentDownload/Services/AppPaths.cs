namespace FastTorrentDownload.Services;

public sealed class AppPaths
{
    public AppPaths()
    {
        RootDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "fast-torrent-download");
        CacheDirectory = Path.Combine(RootDirectory, "cache");
        LogsDirectory = Path.Combine(RootDirectory, "logs");
        SettingsFile = Path.Combine(RootDirectory, "settings.json");
        EngineStateFile = Path.Combine(RootDirectory, "engine-state.json");
    }

    public string RootDirectory { get; }
    public string CacheDirectory { get; }
    public string LogsDirectory { get; }
    public string SettingsFile { get; }
    public string EngineStateFile { get; }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(CacheDirectory);
    }
}
