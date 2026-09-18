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
    public int MaximumDownloadKiBPerSecond { get; set; }
    public int MaximumUploadKiBPerSecond { get; set; }
    public double SeedRatioTarget { get; set; } = 1.0;
    public string Theme { get; set; } = "System";
    public bool ConfirmCloseBeforeExit { get; set; } = true;
    public string UpdateRepository { get; set; } = "stuckinowhere/fast-torrent-download";
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
        SeedRatioTarget = Math.Max(0, SeedRatioTarget);
        Theme = Theme is "Light" or "Dark" or "System" ? Theme : "System";
        PausedInfoHashes = new HashSet<string>(
            PausedInfoHashes.Where(hash => !string.IsNullOrWhiteSpace(hash)),
            StringComparer.OrdinalIgnoreCase);
    }
}
