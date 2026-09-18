namespace FastTorrentDownload.Services;

// Connection and cache limits tuned above the MonoTorrent defaults (150 engine
// connections, 8 half-open, 60 per-torrent connections, 5 MiB write cache) so
// well-seeded swarms ramp up faster on typical broadband. Verified against
// MonoTorrent 3.0.2; revisit if the package default set changes.
public static class EnginePerformanceTuning
{
    public const int MaxConnections = 500;
    public const int MaxHalfOpenConnections = 50;
    public const int MaxConnectionsPerTorrent = 100;
    public const int DiskCacheBytes = 32 * 1024 * 1024;
    public static readonly TimeSpan WebSeedDelay = TimeSpan.FromSeconds(15);
}
