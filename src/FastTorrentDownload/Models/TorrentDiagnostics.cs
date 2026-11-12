using FastTorrentDownload.Services;

namespace FastTorrentDownload.Models;

public sealed record TrackerDiagnostic(string Address, string State, string? Message);

public sealed record TorrentDiagnostics(
    int KnownPeers, int ConnectedPeers, int ReceivingPeers, int ConnectedSeeders,
    double? AvailablePiecesPercent, string DhtState, int DhtNodes,
    long DownloadLimit, long UploadLimit, IReadOnlyList<TrackerDiagnostic> Trackers,
    string? PeerError = null, string? TorrentError = null,
    int IncomingPeers = 0, int ListenPort = 51413,
    int? SwarmSeeders = null, int? SwarmLeechers = null, TimeSpan? StalledFor = null)
{
    public bool IsInboundPortLikelyClosed => ConnectedPeers >= 5 && IncomingPeers == 0;
    public bool IsStalled => StalledFor is not null;
    // Trackers report no seeder and none is connected: nobody reachable holds the complete torrent.
    public bool HasNoSeeders => SwarmSeeders == 0 && ConnectedSeeders == 0;

    /// <summary>One-line reason for the row and the notification; null while data is flowing.</summary>
    public string? StallSummary
    {
        get
        {
            if (!IsStalled) return null;
            if (HasNoSeeders)
            {
                var waiting = SwarmLeechers is > 0 ? $" · {SwarmLeechers} other downloaders waiting" : string.Empty;
                var held = AvailablePiecesPercent is { } percent and < 100 ? $" · connected peers hold {percent:0.0}%" : string.Empty;
                return $"No seeders in the swarm{waiting}{held}";
            }

            if (ConnectedPeers == 0) return KnownPeers == 0 ? "No peers found" : "No peer accepts a connection";
            return AvailablePiecesPercent is < 100
                ? $"Connected peers hold only {AvailablePiecesPercent:0.0}% of the data"
                : "Connected peers are not sending data";
        }
    }

    public string Describe(long downloadRate)
    {
        var peers = PeerError is null
            ? $"Known peers: {KnownPeers} · connected: {ConnectedPeers} (incoming on port {ListenPort}: {IncomingPeers}) · sending data to us: {ReceivingPeers} · connected seeders: {ConnectedSeeders}"
            : $"Peer statistics unavailable: {PeerError}";
        var portStatus = PeerError is null && IsInboundPortLikelyClosed
            ? $"\nInbound port {ListenPort} (TCP/UDP): 0 incoming peers — open port {ListenPort} in your router (Port Forwarding) to allow all seeders to connect."
            : string.Empty;
        var coverage = AvailablePiecesPercent is { } percent ? $"{percent:0.0}%" : "unknown";
        var swarm = SwarmSeeders is { } swarmSeeders
            ? $"Swarm reported by trackers: {swarmSeeders} seeders · {SwarmLeechers ?? 0} downloaders"
            : "Swarm reported by trackers: not available yet";
        var trackers = Trackers.Count == 0 ? "No trackers; discovery depends on DHT / peer exchange."
            : string.Join(Environment.NewLine, Trackers.Select(tracker => $"{tracker.Address} · {tracker.State}{(string.IsNullOrWhiteSpace(tracker.Message) ? "" : " · " + tracker.Message)}"));
        return $"{Diagnosis(downloadRate)}\n{peers}{portStatus}\n{swarm}\nPiece coverage (local + connected peers): {coverage}\nDHT: {DhtState} · {DhtNodes} nodes\nEffective download limit: {Limit(DownloadLimit)} · upload limit: {Limit(UploadLimit)}\nTorrent / disk error: {TorrentError ?? "none reported"}\nTrackers:\n{trackers}";
    }

    public string Diagnosis(long downloadRate)
    {
        if (TorrentError is not null) return "The torrent reports an error; inspect the details below.";
        if (PeerError is not null) return "Peer statistics could not be read; the bottleneck is unknown.";
        if (IsStalled)
        {
            return HasNoSeeders
                ? $"Stalled: {StallSummary}. The download cannot finish until a seeder comes back; look for another source of this torrent."
                : $"Stalled for {(int)StalledFor!.Value.TotalMinutes} min: {StallSummary}.";
        }
        if (ConnectedPeers == 0) return KnownPeers == 0 ? "No known peers currently available." : "Peer candidates exist, but none are connected.";
        if (ReceivingPeers == 0) return "Peers are connected, but none are currently sending us data.";
        if (DownloadLimit > 0 && downloadRate >= DownloadLimit * 0.9) return "Download speed is close to the configured limit.";
        if (AvailablePiecesPercent is < 100) return "Local data and connected peers do not currently cover all pieces.";
        if (IsInboundPortLikelyClosed) return $"Data is flowing, but inbound port {ListenPort} appears closed (0 incoming peers) — opening it in your router can significantly increase speed.";
        return "Data is flowing; these indicators do not establish a single speed bottleneck.";
    }

    private static string Limit(long value) => value <= 0 ? "unlimited" : TransferFormatting.Rate(value);
}
