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
                var waiting = SwarmLeechers is > 0 ? Loc.I($" · {SwarmLeechers} other downloaders waiting") : string.Empty;
                var held = AvailablePiecesPercent is { } percent and < 100 ? Loc.I($" · connected peers hold {percent:0.0}%") : string.Empty;
                return Loc.T("No seeders in the swarm") + waiting + held;
            }

            if (ConnectedPeers == 0) return KnownPeers == 0 ? Loc.T("No peers found") : Loc.T("No peer accepts a connection");
            return AvailablePiecesPercent is < 100
                ? Loc.I($"Connected peers hold only {AvailablePiecesPercent:0.0}% of the data")
                : Loc.T("Connected peers are not sending data");
        }
    }

    public string Describe(long downloadRate)
    {
        var peers = PeerError is null
            ? Loc.I($"Known peers: {KnownPeers} · connected: {ConnectedPeers} (incoming on port {ListenPort}: {IncomingPeers}) · sending data to us: {ReceivingPeers} · connected seeders: {ConnectedSeeders}")
            : Loc.I($"Peer statistics unavailable: {PeerError}");
        var portStatus = PeerError is null && IsInboundPortLikelyClosed
            ? "\n" + Loc.I($"Inbound port {ListenPort} (TCP/UDP): 0 incoming peers — open port {ListenPort} in your router (Port Forwarding) to allow all seeders to connect.")
            : string.Empty;
        var coverage = AvailablePiecesPercent is { } percent ? $"{percent:0.0}%" : Loc.T("unknown");
        var swarm = SwarmSeeders is { } swarmSeeders
            ? Loc.I($"Swarm reported by trackers: {swarmSeeders} seeders · {SwarmLeechers ?? 0} downloaders")
            : Loc.T("Swarm reported by trackers: not available yet");
        var trackers = Trackers.Count == 0 ? Loc.T("No trackers; discovery depends on DHT / peer exchange.")
            : string.Join(Environment.NewLine, Trackers.Select(tracker => $"{tracker.Address} · {Loc.Known(tracker.State)}{(string.IsNullOrWhiteSpace(tracker.Message) ? "" : " · " + tracker.Message)}"));
        var dht = Loc.Known(DhtState);
        var downloadLimit = Limit(DownloadLimit);
        var uploadLimit = Limit(UploadLimit);
        var error = TorrentError ?? Loc.T("none reported");
        return string.Join("\n",
            Diagnosis(downloadRate),
            peers + portStatus,
            swarm,
            Loc.I($"Piece coverage (local + connected peers): {coverage}"),
            Loc.I($"DHT: {dht} · {DhtNodes} nodes"),
            Loc.I($"Effective download limit: {downloadLimit} · upload limit: {uploadLimit}"),
            Loc.I($"Torrent / disk error: {error}"),
            Loc.T("Trackers:"),
            trackers);
    }

    public string Diagnosis(long downloadRate)
    {
        if (TorrentError is not null) return Loc.T("The torrent reports an error; inspect the details below.");
        if (PeerError is not null) return Loc.T("Peer statistics could not be read; the bottleneck is unknown.");
        if (IsStalled)
        {
            var minutes = (int)StalledFor!.Value.TotalMinutes;
            return HasNoSeeders
                ? Loc.I($"Stalled: {StallSummary}. The download cannot finish until a seeder comes back; look for another source of this torrent.")
                : Loc.I($"Stalled for {minutes} min: {StallSummary}.");
        }
        if (ConnectedPeers == 0) return KnownPeers == 0 ? Loc.T("No known peers currently available.") : Loc.T("Peer candidates exist, but none are connected.");
        if (ReceivingPeers == 0) return Loc.T("Peers are connected, but none are currently sending us data.");
        if (DownloadLimit > 0 && downloadRate >= DownloadLimit * 0.9) return Loc.T("Download speed is close to the configured limit.");
        if (AvailablePiecesPercent is < 100) return Loc.T("Local data and connected peers do not currently cover all pieces.");
        if (IsInboundPortLikelyClosed) return Loc.I($"Data is flowing, but inbound port {ListenPort} appears closed (0 incoming peers) — opening it in your router can significantly increase speed.");
        return Loc.T("Data is flowing; these indicators do not establish a single speed bottleneck.");
    }

    private static string Limit(long value) => value <= 0 ? Loc.T("unlimited") : TransferFormatting.Rate(value);
}
