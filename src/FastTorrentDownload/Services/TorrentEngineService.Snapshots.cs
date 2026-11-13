using System.Net;
using System.Collections.Concurrent;
using FastTorrentDownload.Models;
using MonoTorrent;
using MonoTorrent.BEncoding;
using MonoTorrent.Client;
using MonoTorrent.Connections;
using MonoTorrent.Trackers;

namespace FastTorrentDownload.Services;

// Per-refresh snapshots: stall detection, swarm scrape, completion and diagnostics.
public sealed partial class TorrentEngineService
{
    public async Task<IReadOnlyList<TorrentSnapshot>> GetSnapshotsAsync(IReadOnlySet<string>? deferredCompletionIds = null)
    {
        KeyValuePair<string, (TorrentManager Manager, string Destination)>[] entries;
        HashSet<string> queued;
        await _gate.WaitAsync();
        try
        {
            if (_engine is not null) await PromoteQueuedAsync();
            entries = _entries.ToArray();
            queued = new HashSet<string>(_queuedHashes, StringComparer.OrdinalIgnoreCase);
        }
        finally { _gate.Release(); }
        var snapshots = new List<TorrentSnapshot>(entries.Length);
        foreach (var pair in entries)
        {
            var manager = pair.Value.Manager;
            if (ShouldRemoveCompletedTorrent(manager.State, manager.PartialProgress) &&
                deferredCompletionIds?.Contains(pair.Key) != true &&
                !_completionErrors.ContainsKey(pair.Key) && _operations.TryAdd(pair.Key, "Stopping…"))
            {
                _automaticRemovals[pair.Key] = CompleteAsync(pair.Key, manager, pair.Value.Destination);
            }

            var monitor = manager.Monitor;
            var downloaded = monitor.DataBytesReceived;
            TimeSpan? stalledFor = null;
            if (manager.State == TorrentState.Downloading)
            {
                var now = DateTimeOffset.UtcNow;
                var payload = _lastPayload.GetOrAdd(pair.Key, (downloaded, now));
                if (payload.Received != downloaded)
                {
                    payload = (downloaded, now);
                    _lastPayload[pair.Key] = payload;
                }

                if (IsStalled(manager.State, now - payload.At))
                {
                    stalledFor = now - payload.At;
                }

                var last = _lastReannounce.GetOrAdd(pair.Key, now);
                if (ShouldReannounceStarvedTorrent(manager.State, manager.OpenConnections, now - last, stalledFor is not null) &&
                    _lastReannounce.TryUpdate(pair.Key, now, last))
                {
                    _ = ReannounceStarvedTorrentAsync(manager);
                }

                // Scrape counts tell a dead swarm (0 seeders) apart from a slow one.
                if (!_lastScrape.TryGetValue(pair.Key, out var scraped) || now - scraped >= ScrapeInterval)
                {
                    _lastScrape[pair.Key] = now;
                    _ = ScrapeSwarmAsync(manager);
                }
            }
            else
            {
                _lastReannounce.TryRemove(pair.Key, out _);
                _lastPayload.TryRemove(pair.Key, out _);
            }

            var (selectedBytes, verifiedBytes) = GetSelectedBytes(manager);
            var uploaded = monitor.DataBytesSent;
            var swarm = ReadSwarm(manager);
            var diagnostics = await GetCachedDiagnosticsAsync(pair.Key, manager) with
            {
                SwarmSeeders = swarm?.Seeders,
                SwarmLeechers = swarm?.Leechers,
                StalledFor = stalledFor
            };
            if (stalledFor is null)
            {
                _stalledIds.TryRemove(pair.Key, out _);
            }
            else if (_stalledIds.TryAdd(pair.Key, 0))
            {
                AppLogger.Log($"Torrent stalled: no payload for {(int)stalledFor.Value.TotalMinutes} min · swarm seeders={swarm?.Seeders.ToString() ?? "unknown"} leechers={swarm?.Leechers.ToString() ?? "unknown"} · connected={diagnostics.ConnectedPeers} connectedSeeders={diagnostics.ConnectedSeeders} · coverage={diagnostics.AvailablePiecesPercent?.ToString("0.0") ?? "unknown"}%");
            }

            var state = manager.State == TorrentState.Stopped && queued.Contains(DescribeInfoHashes(manager.InfoHashes))
                ? "Queued"
                : stalledFor is not null ? "Stalled" : manager.State.ToString();
            snapshots.Add(new TorrentSnapshot(
                pair.Key,
                string.IsNullOrWhiteSpace(manager.Name) ? "Fetching metadata…" : manager.Name,
                pair.Value.Destination,
                state,
                selectedBytes > 0 ? 100.0 * verifiedBytes / selectedBytes : manager.PartialProgress,
                monitor.DownloadRate,
                monitor.UploadRate,
                downloaded,
                uploaded,
                diagnostics.ConnectedSeeders,
                diagnostics.ConnectedPeers - diagnostics.ConnectedSeeders,
                _operations.GetValueOrDefault(pair.Key),
                selectedBytes,
                verifiedBytes,
                diagnostics,
                _sequentialHashes.Contains(DescribeInfoHashes(manager.InfoHashes))));
        }

        // "Stalled" is deliberately absent: a dead swarm must not keep the computer awake.
        _sleepGuard.Update(snapshots.Any(s => s.State is "Downloading" or "Metadata" or "Hashing" or "Starting"));
        return snapshots;
    }

    private async Task<TorrentDiagnostics> GetCachedDiagnosticsAsync(string id, TorrentManager manager)
    {
        var now = DateTimeOffset.UtcNow;
        if (_diagnosticsCache.TryGetValue(id, out var cached) && now - cached.At < DiagnosticsInterval)
        {
            return cached.Value;
        }

        var diagnostics = await GetDiagnosticsAsync(id, manager);
        _diagnosticsCache[id] = (now, diagnostics);
        return diagnostics;
    }

    public static bool ShouldReannounceStarvedTorrent(TorrentState state, int openConnections, TimeSpan elapsedSinceLastAnnounce, bool stalled = false) =>
        state == TorrentState.Downloading &&
        openConnections < EnginePerformanceTuning.LowPeerThreshold &&
        elapsedSinceLastAnnounce >= (stalled ? EnginePerformanceTuning.StalledReannounceInterval : EnginePerformanceTuning.ReannounceInterval);

    public static bool IsStalled(TorrentState state, TimeSpan sinceLastPayload) =>
        state == TorrentState.Downloading && sinceLastPayload >= StallThreshold;

    private static async Task ScrapeSwarmAsync(TorrentManager manager)
    {
        try
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await manager.TrackerManager.ScrapeAsync(budget.Token);
        }
        catch
        {
            // Swarm counts are advisory; a failed scrape leaves them unknown.
        }
    }

    // Best figure across tiers: trackers disagree, and one live seeder anywhere is enough.
    private static (int Seeders, int Leechers)? ReadSwarm(TorrentManager manager)
    {
        (int Seeders, int Leechers)? best = null;
        foreach (var tier in manager.TrackerManager.Tiers)
        {
            if (tier.ScrapeInfo.TryGetValue(manager.InfoHashes.V1OrV2, out var info))
            {
                best = (Math.Max(best?.Seeders ?? 0, info.Complete), Math.Max(best?.Leechers ?? 0, info.Incomplete));
            }
        }

        return best;
    }

    private static async Task ReannounceStarvedTorrentAsync(TorrentManager manager)
    {
        try
        {
            await manager.TrackerManager.AnnounceAsync(CancellationToken.None);
            if (manager.CanUseDht)
            {
                await manager.DhtAnnounceAsync();
            }
        }
        catch
        {
            // Background swarm refresh must never disrupt active transfers.
        }
    }

    private async Task CompleteAsync(string id, TorrentManager manager, string destination)
    {
        try
        {
            await RemoveCoreAsync(id, manager);
            _completions.Enqueue(new TorrentCompletion(manager.Name, destination));
        }
        catch (Exception exception)
        {
            _completionErrors[id] = exception.Message;
            _completions.Enqueue(new TorrentCompletion(manager.Name, destination, exception.Message));
            AppLogger.LogException("Automatic removal failed", exception);
        }
        finally
        {
            _operations.TryRemove(id, out _);
        }
    }

    public IReadOnlyList<TorrentCompletion> TakeCompletions()
    {
        var completed = new List<TorrentCompletion>();
        while (_completions.TryDequeue(out var completion)) completed.Add(completion);
        foreach (var item in _automaticRemovals.Where(item => item.Value.IsCompleted))
            _automaticRemovals.TryRemove(item.Key, out _);
        return completed;
    }

    public static bool ShouldRemoveCompletedTorrent(TorrentState state, double partialProgress) =>
        state is TorrentState.Downloading or TorrentState.Seeding && partialProgress >= 100;

    public static (long Selected, long Verified) GetSelectedBytes(TorrentManager manager)
    {
        if (manager.Torrent is not null && manager.Files.Count > 0 && manager.Files.All(file => file.Priority != Priority.DoNotDownload))
        {
            var total = manager.Torrent.Size;
            if (manager.Bitfield.AllTrue) return (total, total);
            if (manager.Bitfield.AllFalse || manager.Bitfield.Length == 0) return (total, 0);
            var pieceLength = (long)manager.Torrent.PieceLength;
            var verifiedBytes = (long)manager.Bitfield.TrueCount * pieceLength;
            if (manager.Bitfield[^1])
            {
                var lastPieceSize = total - (long)(manager.Bitfield.Length - 1) * pieceLength;
                verifiedBytes -= pieceLength - Math.Max(0, lastPieceSize);
            }
            return (total, Math.Clamp(verifiedBytes, 0, total));
        }

        long selected = 0, verified = 0;
        foreach (var file in manager.Files.Where(file => file.Priority != Priority.DoNotDownload))
        {
            selected += file.Length;
            if (file.BitField.AllTrue) { verified += file.Length; continue; }
            if (file.BitField.AllFalse || manager.Torrent is null) continue;
            // ponytail: scan partially verified files; cache piece-byte totals if million-piece torrents make this costly.
            for (var index = 0; index < file.BitField.Length; index++)
            {
                if (!file.BitField[index]) continue;
                var start = (long)(file.StartPieceIndex + index) * manager.Torrent.PieceLength;
                verified += Math.Max(0, Math.Min(start + manager.Torrent.PieceLength, file.OffsetInTorrent + file.Length) - Math.Max(start, file.OffsetInTorrent));
            }
        }
        return (selected, verified);
    }

    private async Task<TorrentDiagnostics> GetDiagnosticsAsync(string id, TorrentManager manager)
    {
        var known = manager.Peers.Available;
        var connected = 0;
        var receiving = 0;
        var seeders = 0;
        var incoming = 0;
        double? coverage = null;
        string? peerError = null;
        try
        {
            var peers = (await manager.GetPeersAsync()).Where(peer => peer.IsConnected).ToArray();
            connected = peers.Length;
            known += connected;
            receiving = peers.Count(peer => peer.Monitor.DownloadRate > 0);
            incoming = peers.Count(peer => peer.ConnectionDirection == Direction.Incoming);
            seeders = peers.Count(peer => peer.IsSeeder);
            if (manager.HasMetadata)
            {
                if (manager.Bitfield.AllTrue || seeders > 0)
                {
                    coverage = 100.0;
                }
                else
                {
                    var pieces = new BitField(manager.Bitfield.Length).Or(manager.Bitfield);
                    foreach (var peer in peers)
                    {
                        if (peer.BitField.Length == pieces.Length) pieces.Or(peer.BitField);
                    }
                    coverage = pieces.PercentComplete;
                }
            }
        }
        catch (Exception exception)
        {
            peerError = exception.Message;
        }
        var trackers = manager.TrackerManager.Tiers.SelectMany(tier => tier.Trackers)
            .Select(tracker => new TrackerDiagnostic(tracker.Uri.GetLeftPart(UriPartial.Authority), tracker.Status.ToString(),
                string.IsNullOrWhiteSpace(tracker.FailureMessage) ? tracker.WarningMessage : tracker.FailureMessage)).ToArray();
        var error = _completionErrors.GetValueOrDefault(id)
            ?? (manager.Error is { } engineError ? $"{engineError.Reason}: {engineError.Exception?.Message}" : null);
        var engine = RequireEngine();
        return new TorrentDiagnostics(known, connected, receiving, seeders, coverage,
            _settings.EnableDht && manager.CanUseDht ? engine.Dht.State.ToString() : "Disabled for this torrent", engine.Dht.NodeCount,
            EffectiveLimit(engine.Settings.MaximumDownloadRate, manager.Settings.MaximumDownloadRate),
            EffectiveLimit(engine.Settings.MaximumUploadRate, manager.Settings.MaximumUploadRate), trackers, peerError, error, incoming, _settings.ListenPort);
    }

    public static long EffectiveLimit(long global, long torrent) => global <= 0 ? torrent : torrent <= 0 ? global : Math.Min(global, torrent);
}
