using FastTorrentDownload.Services;

namespace FastTorrentDownload.Models;

public sealed record TrackerDiagnostic(string Address, string State, string? Message);

public sealed record TorrentDiagnostics(
    int KnownPeers, int ConnectedPeers, int ReceivingPeers, int ConnectedSeeders,
    double? AvailablePiecesPercent, string DhtState, int DhtNodes,
    long DownloadLimit, long UploadLimit, IReadOnlyList<TrackerDiagnostic> Trackers,
    string? PeerError = null, string? TorrentError = null)
{
    public string Describe(long downloadRate)
    {
        var peers = PeerError is null
            ? $"Known peers: {KnownPeers} · connected: {ConnectedPeers} · sending data to us: {ReceivingPeers} · connected seeders: {ConnectedSeeders}"
            : $"Peer statistics unavailable: {PeerError}";
        var coverage = AvailablePiecesPercent is { } percent ? $"{percent:0.0}%" : "unknown";
        var trackers = Trackers.Count == 0 ? "No trackers; discovery depends on DHT / peer exchange."
            : string.Join(Environment.NewLine, Trackers.Select(tracker => $"{tracker.Address} · {tracker.State}{(string.IsNullOrWhiteSpace(tracker.Message) ? "" : " · " + tracker.Message)}"));
        return $"{Diagnosis(downloadRate)}\n{peers}\nPiece coverage (local + connected peers): {coverage}\nDHT: {DhtState} · {DhtNodes} nodes\nEffective download limit: {Limit(DownloadLimit)} · upload limit: {Limit(UploadLimit)}\nTorrent / disk error: {TorrentError ?? "none reported"}\nTrackers:\n{trackers}";
    }

    public string Diagnosis(long downloadRate)
    {
        if (TorrentError is not null) return "The torrent reports an error; inspect the details below.";
        if (PeerError is not null) return "Peer statistics could not be read; the bottleneck is unknown.";
        if (ConnectedPeers == 0) return KnownPeers == 0 ? "No known peers currently available." : "Peer candidates exist, but none are connected.";
        if (ReceivingPeers == 0) return "Peers are connected, but none are currently sending us data.";
        if (DownloadLimit > 0 && downloadRate >= DownloadLimit * 0.9) return "Download speed is close to the configured limit.";
        if (AvailablePiecesPercent is < 100) return "Local data and connected peers do not currently cover all pieces.";
        return "Data is flowing; these indicators do not establish a single speed bottleneck.";
    }

    private static string Limit(long value) => value <= 0 ? "unlimited" : TransferFormatting.Rate(value);
}
