using MonoTorrent.Connections;

namespace FastTorrentDownload.Services;

// Connection and cache limits tuned above the MonoTorrent defaults (150 engine
// connections, 8 half-open, 60 per-torrent connections, 5 MiB write cache) so
// well-seeded swarms and NAT-heavy public swarms ramp up faster on broadband.
// Verified against MonoTorrent 3.0.2; revisit if the package default set changes.
public static class EnginePerformanceTuning
{
    public const int MaxConnections = 800;
    public const int MaxHalfOpenConnections = 150;
    public const int MaxConnectionsPerTorrent = 250;
    public const int DiskCacheBytes = 64 * 1024 * 1024;
    public const int UploadSlots = 16;
    public static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(7);
    public static readonly TimeSpan WebSeedDelay = TimeSpan.FromSeconds(15);

    // MonoTorrent attempts outgoing handshakes in the order listed in AllowedEncryption.
    // Prefer RC4 encryption before PlainText even when encryption is not strictly required
    // so peers that require MSE/PE accept the first handshake and ISP DPI cannot easily
    // throttle cleartext BitTorrent handshakes.
    public static IReadOnlyList<EncryptionType> GetAllowedEncryption(bool requireEncrypted) =>
        requireEncrypted
            ? [EncryptionType.RC4Header, EncryptionType.RC4Full]
            : [EncryptionType.RC4Header, EncryptionType.RC4Full, EncryptionType.PlainText];
}
