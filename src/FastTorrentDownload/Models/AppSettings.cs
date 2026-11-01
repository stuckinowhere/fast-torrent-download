namespace FastTorrentDownload.Models;

public sealed class AppSettings
{
    public string DefaultDownloadFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    public int ListenPort { get; set; } = 51413;
    public string ListenAddress { get; set; } = "0.0.0.0";
    public bool EnableDht { get; set; } = true;
    public bool EnablePeerExchange { get; set; } = true;
    public bool EnableLocalPeerDiscovery { get; set; } = false;
    public bool EnablePortForwarding { get; set; } = true;
    public bool RequireEncryptedPeerConnections { get; set; }
    /// <summary>Opt-in: public .torrent caches learn the info-hash of every magnet looked up.</summary>
    public bool UsePublicMetadataCaches { get; set; }
    public int MaximumDownloadKiBPerSecond { get; set; }
    public int MaximumUploadKiBPerSecond { get; set; }
    /// <summary>0 = unlimited. Torrents beyond the limit wait as "Queued" until a slot frees up.</summary>
    public int MaximumActiveDownloads { get; set; }
    public string Theme { get; set; } = "System";
    public bool ConfirmCloseBeforeExit { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public HashSet<string> PausedInfoHashes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public AppSettings Copy()
    {
        var clone = (AppSettings)MemberwiseClone();
        clone.PausedInfoHashes = new HashSet<string>(PausedInfoHashes, StringComparer.OrdinalIgnoreCase);
        return clone;
    }

    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(DefaultDownloadFolder))
        {
            DefaultDownloadFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }

        ListenPort = Math.Clamp(ListenPort, 1024, 65535);
        ListenAddress = string.IsNullOrWhiteSpace(ListenAddress) ? "0.0.0.0" : ListenAddress.Trim();
        MaximumDownloadKiBPerSecond = Math.Max(0, MaximumDownloadKiBPerSecond);
        MaximumUploadKiBPerSecond = Math.Max(0, MaximumUploadKiBPerSecond);
        MaximumActiveDownloads = Math.Max(0, MaximumActiveDownloads);
        Theme = Theme is "Light" or "Dark" or "System" ? Theme : "System";
        PausedInfoHashes = new HashSet<string>(
            PausedInfoHashes.Where(hash => !string.IsNullOrWhiteSpace(hash)),
            StringComparer.OrdinalIgnoreCase);
    }
}
