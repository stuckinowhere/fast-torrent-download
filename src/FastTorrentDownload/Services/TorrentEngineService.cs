using System.Net;
using System.Collections.Concurrent;
using FastTorrentDownload.Models;
using MonoTorrent;
using MonoTorrent.BEncoding;
using MonoTorrent.Client;
using MonoTorrent.Connections;
using MonoTorrent.Trackers;

namespace FastTorrentDownload.Services;

public sealed class TorrentEngineService : IAsyncDisposable
{
    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (TorrentManager Manager, string Destination)> _entries = new();
    private readonly Dictionary<string, (TorrentManager Manager, string Destination)> _pending = new();
    private ClientEngine? _engine;
    private AppSettings _settings = new();
    private readonly HashSet<string> _pausedHashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _operations = new();
    private readonly ConcurrentDictionary<string, Task> _automaticRemovals = new();
    private readonly ConcurrentDictionary<string, string> _completionErrors = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastReannounce = new();
    private readonly ConcurrentQueue<TorrentCompletion> _completions = new();
    public static TimeSpan StopAnnounceTimeout { get; } = TimeSpan.FromSeconds(2);
    private bool _keepingSystemAwake;
    private bool _disposed;

    public TorrentEngineService(AppPaths paths) => _paths = paths;

    public async Task InitializeAsync(AppSettings settings)
    {
        await _gate.WaitAsync();
        try
        {
            ThrowIfDisposed();
            if (_engine is not null)
            {
                return;
            }

            _settings = settings.Copy();
            _settings.Normalize();
            _pausedHashes.UnionWith(_settings.PausedInfoHashes);
            _paths.EnsureDirectories();
            if (_settings.EnableDht) await DhtBootstrapCache.EnsureSeededAsync(_paths.CacheDirectory);
            if (File.Exists(_paths.EngineStateFile))
            {
                try
                {
                    var buffer = await File.ReadAllBytesAsync(_paths.EngineStateFile);
                    _engine = await ClientEngine.RestoreStateAsync(buffer);
                    var saved = BEncodedValue.Decode<BEncodedDictionary>(buffer);
                    if (saved.TryGetValue("PausedInfoHashes", out var paused) && paused is BEncodedList hashes)
                    {
                        _pausedHashes.Clear();
                        _pausedHashes.UnionWith(hashes.Cast<BEncodedString>().Select(hash => hash.Text));
                    }
                }
                catch (Exception exception) when (IsRecoverableStateException(exception))
                {
                    // A corrupt or incompatible cache must never prevent the client from opening.
                }
            }

            _engine ??= new ClientEngine(CreateEngineSettings(_settings));
            await _engine.UpdateSettingsAsync(CreateEngineSettings(_settings));
            AppLogger.Log($"Engine initialized: listen={_settings.ListenAddress}:{_settings.ListenPort} dht={_settings.EnableDht} pex={_settings.EnablePeerExchange} lpd={_settings.EnableLocalPeerDiscovery} upnp={_settings.EnablePortForwarding} restored={_engine.Torrents.Count} dhtState={_engine.Dht.State} dhtNodes={_engine.Dht.NodeCount} tuning={EnginePerformanceTuning.MaxConnections}c/{EnginePerformanceTuning.MaxHalfOpenConnections}half/{EnginePerformanceTuning.MaxConnectionsPerTorrent}pt");

            foreach (var manager in _engine.Torrents)
            {
                Track(manager, manager.SavePath);
                await manager.UpdateSettingsAsync(CreateTorrentSettings(_settings));
                await UpgradeLegacyTrackersAsync(manager);
                await SplitTrackerTiersAsync(manager);
            }

            var repaused = 0;
            foreach (var manager in _engine.Torrents)
            {
                if (_pausedHashes.Contains(DescribeInfoHashes(manager.InfoHashes)))
                {
                    repaused++;
                }
                else
                {
                    await manager.StartAsync();
                }
            }

            AppLogger.Log($"Engine started: resumed {_engine.Torrents.Count} restored torrent(s), kept {repaused} paused.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TorrentAddPreview> PrepareAddAsync(string source, string? destination, CancellationToken cancellationToken = default)
    {
        ClientEngine? engine = null;
        TorrentManager manager;
        string safeDestination;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            engine = RequireEngine();
            var safeSource = PathGuard.ResolveTorrentSource(source);
            safeDestination = PathGuard.ResolveDestination(_settings.DefaultDownloadFolder, destination);
            manager = safeSource.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)
                ? await engine.AddAsync(MagnetLink.Parse(safeSource), safeDestination, CreateTorrentSettings(_settings))
                : await engine.AddAsync(Torrent.Load(safeSource), safeDestination, CreateTorrentSettings(_settings));
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            if (!manager.HasMetadata)
            {
                await SeedDefaultTrackersAsync(manager);
                await PruneEmptyTrackersAsync(manager, cancellationToken);
                await SplitTrackerTiersAsync(manager);
                await manager.StartAsync();
                try
                {
                    // No fixed timeout: thin swarms can take several minutes of DHT
                    // sampling before a metadata holder is found (uTorrent waits
                    // indefinitely too). The caller cancels via the token — the file
                    // picker wires its Cancel button and window close to it.
                    await WaitForMetadataWithTrackerRotationAsync(manager, cancellationToken);
                }
                finally
                {
                    await manager.StopAsync();
                }
            }

            var id = Guid.NewGuid().ToString("N");
            await _gate.WaitAsync(cancellationToken);
            try
            {
                _pending[id] = (manager, safeDestination);
            }
            finally
            {
                _gate.Release();
            }

            return new TorrentAddPreview(
                id,
                string.IsNullOrWhiteSpace(manager.Name) ? "Torrent" : manager.Name,
                manager.Files.Select(file => new TorrentFilePreview(file.Path, file.Length)).ToArray());
        }
        catch (Exception exception)
        {
            AppLogger.LogException("PrepareAdd failed", exception);
            await engine!.RemoveAsync(manager);
            throw;
        }
    }

    private static async Task WaitForMetadataWithTrackerRotationAsync(TorrentManager manager, CancellationToken cancellationToken)
    {
        // MonoTorrent announces to a single tracker until one succeeds — even when the
        // response holds zero peers — so a first-tried empty tracker starves the fetch
        // while sibling trackers hold live peers, and restarts re-announce to the same
        // tracker. A starved fetch therefore drops the empty tracker and restarts
        // (bounded), deterministically walking the list until peers arrive.
        const int maxRotations = 5;
        var rotations = 0;
        var firstSlice = true;
        while (!manager.HasMetadata)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var slice = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            slice.CancelAfter(firstSlice ? TimeSpan.FromSeconds(20) : TimeSpan.FromSeconds(15));
            try
            {
                await manager.WaitForMetadataAsync(slice.Token);
                return;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Slice expired with metadata still missing; fall through to the rotation.
            }

            if (!firstSlice && rotations < maxRotations && await RotateStarvedTrackersAsync(manager, cancellationToken))
            {
                rotations++;
            }

            firstSlice = false;
        }
    }

    // Rescue for trackerless magnets: without trackers the fetch relies solely on DHT,
    // which is slow and unreliable on a cold start. Every entry here was verified
    // reachable; the scrape preflight below prunes any that report no peers for the
    // torrent, so a dead entry only costs a few seconds.
    public static IReadOnlyList<Uri> DefaultPublicTrackers { get; } =
    [
        new("udp://tracker.opentrackr.org:1337/announce"),
        new("udp://open.stealth.si:80/announce"),
        new("udp://tracker.torrent.eu.org:451/announce"),
        new("udp://open.demonii.com:1337/announce"),
        new("udp://exodus.desync.com:6969/announce"),
        new("udp://explodie.org:6969/announce"),
    ];

    private static readonly HashSet<Uri> LegacyDeadDefaultTrackers =
    [
        new("https://opentracker.io/announce"),
        new("https://torrent.eu.org/announce.php"),
    ];

    private static async Task SeedDefaultTrackersAsync(TorrentManager manager)
    {
        var existing = manager.TrackerManager.Tiers
            .SelectMany(tier => tier.Trackers)
            .Select(tracker => tracker.Uri)
            .ToHashSet();
        var added = 0;
        foreach (var uri in DefaultPublicTrackers)
        {
            if (existing.Contains(uri))
            {
                continue;
            }

            try
            {
                await manager.TrackerManager.AddTrackerAsync(uri);
                added++;
            }
            catch (Exception exception)
            {
                // A bad default entry must never break the add; DHT remains.
                AppLogger.Log($"Default tracker: skipping {uri} ({exception.GetType().Name}).");
            }
        }

        if (added > 0)
        {
            AppLogger.Log($"Default tracker: seeded {added} public trackers (total {CountTrackers(manager)}).");
        }
    }

    private static async Task UpgradeLegacyTrackersAsync(TorrentManager manager)
    {
        var trackers = manager.TrackerManager.Tiers.SelectMany(tier => tier.Trackers).ToList();
        var legacy = trackers.Where(tracker => LegacyDeadDefaultTrackers.Contains(tracker.Uri)).ToList();
        if (legacy.Count == 0)
        {
            return;
        }

        foreach (var tracker in legacy)
        {
            await manager.TrackerManager.RemoveTrackerAsync(tracker);
        }

        await SeedDefaultTrackersAsync(manager);
    }

    private static async Task SplitTrackerTiersAsync(TorrentManager manager)
    {
        foreach (var tier in manager.TrackerManager.Tiers.ToList())
        {
            if (tier.Trackers.Count <= 1)
            {
                continue;
            }

            foreach (var tracker in tier.Trackers.Skip(1).ToList())
            {
                try
                {
                    await manager.TrackerManager.RemoveTrackerAsync(tracker);
                    await manager.TrackerManager.AddTrackerAsync(tracker.Uri);
                }
                catch
                {
                    // Best-effort tier split must never break add or restore.
                }
            }
        }
    }

    private static async Task PruneEmptyTrackersAsync(TorrentManager manager, CancellationToken cancellationToken)
    {
        var trackers = manager.TrackerManager.Tiers.SelectMany(tier => tier.Trackers).ToList();
        if (trackers.Count <= 1)
        {
            return;
        }

        // Scrape sequentially: trackers in one tier share a single scrape entry, so a
        // scrape must be read before the next one overwrites it. The whole preflight
        // stays within an overall budget so a long tracker list cannot stall the add.
        var results = new List<(Uri Uri, bool Scraped, int Complete, int Incomplete)>(trackers.Count);
        using var totalBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        totalBudget.CancelAfter(TimeSpan.FromSeconds(10));
        foreach (var tracker in trackers)
        {
            if (totalBudget.IsCancellationRequested)
            {
                break;
            }

            try
            {
                results.Add(await ScrapeTrackerAsync(manager, tracker, totalBudget.Token));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        foreach (var uri in SelectTrackersToPrune(results))
        {
            var tracker = trackers.First(candidate => candidate.Uri == uri);
            await manager.TrackerManager.RemoveTrackerAsync(tracker);
        }
    }

    private static async Task<(Uri Uri, bool Scraped, int Complete, int Incomplete)> ScrapeTrackerAsync(
        TorrentManager manager, ITracker tracker, CancellationToken cancellationToken)
    {
        if (!tracker.CanScrape)
        {
            return (tracker.Uri, false, 0, 0);
        }

        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TimeSpan.FromSeconds(3));
            await manager.TrackerManager.ScrapeAsync(tracker, budget.Token);
            var tier = manager.TrackerManager.Tiers.First(candidate => candidate.Trackers.Contains(tracker));
            if (tier.ScrapeInfo.TryGetValue(manager.InfoHashes.V1OrV2, out var info))
            {
                return (tracker.Uri, true, info.Complete, info.Incomplete);
            }

            return (tracker.Uri, false, 0, 0);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // A failed scrape must never strand the fetch: keep the tracker.
            return (tracker.Uri, false, 0, 0);
        }
    }

    public static IReadOnlyList<Uri> SelectTrackersToPrune(IReadOnlyList<(Uri Uri, bool Scraped, int Complete, int Incomplete)> scrapes)
    {
        // Drop proven-empty trackers, but never strand the torrent: trackers whose
        // scrape failed stay, and when nothing reports peers everything stays.
        var keeperUris = scrapes
            .Where(scrape => !scrape.Scraped || scrape.Complete > 0 || scrape.Incomplete > 0)
            .Select(scrape => scrape.Uri)
            .ToHashSet();
        if (keeperUris.Count == 0)
        {
            return [];
        }

        return scrapes.Select(scrape => scrape.Uri).Where(uri => !keeperUris.Contains(uri)).ToList();
    }

    public static bool ShouldRotateTrackerCoverage(int knownPeers, int openConnections) =>
        knownPeers < 2 && openConnections == 0;

    private static async Task<bool> RotateStarvedTrackersAsync(TorrentManager manager, CancellationToken cancellationToken)
    {
        // A lone connected peer can still deliver metadata: only rotate when nothing
        // is flowing, otherwise the restart would kill the only useful connection.
        if (!ShouldRotateTrackerCoverage((await manager.GetPeersAsync()).Count, manager.OpenConnections))
        {
            return false;
        }

        // Drop the active (empty) tracker in every tier that can spare one, then
        // restart so the failover announces to the next tracker with merged peers.
        // A bare restart would re-announce to the same tracker, achieving nothing.
        var removed = false;
        foreach (var tier in manager.TrackerManager.Tiers)
        {
            if (tier.Trackers.Count > 1 && tier.ActiveTracker is not null)
            {
                await manager.TrackerManager.RemoveTrackerAsync(tier.ActiveTracker);
                removed = true;
            }
        }

        if (!removed)
        {
            return false;
        }

        await manager.StopAsync();
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        await manager.StartAsync();
        return true;
    }

    private static string DescribeInfoHashes(InfoHashes hashes) => hashes.V1OrV2.ToHex();

    private static int CountTrackers(TorrentManager manager) =>
        manager.TrackerManager.Tiers.SelectMany(tier => tier.Trackers).Count();

    public async Task CommitAddAsync(
        TorrentAddPreview preview,
        IReadOnlyCollection<string> selectedFiles,
        bool startImmediately)
    {
        if (selectedFiles.Count == 0)
        {
            throw new ArgumentException("Select at least one file to download.", nameof(selectedFiles));
        }

        await _gate.WaitAsync();
        try
        {
            if (!_pending.TryGetValue(preview.Id, out var pending))
            {
                throw new KeyNotFoundException("That torrent preview is no longer available.");
            }

            var selected = selectedFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var file in pending.Manager.Files)
            {
                var priority = selected.Contains(file.Path) ? Priority.Normal : Priority.DoNotDownload;
                await pending.Manager.SetFilePriorityAsync(file, priority);
            }

            _pending.Remove(preview.Id);
            _entries[preview.Id] = pending;
            var hash = DescribeInfoHashes(pending.Manager.InfoHashes);
            _pausedHashes.Remove(hash);
            if (!startImmediately)
            {
                _pausedHashes.Add(hash);
            }
            try
            {
                await SaveStateAsync();
            }
            catch
            {
                _entries.Remove(preview.Id);
                _pending[preview.Id] = pending;
                _pausedHashes.Remove(hash);
                throw;
            }
            await SplitTrackerTiersAsync(pending.Manager);
            if (startImmediately) await pending.Manager.StartAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CancelAddAsync(string id)
    {
        await _gate.WaitAsync();
        try
        {
            var engine = RequireEngine();
            if (_pending.Remove(id, out var pending))
            {
                await engine.RemoveAsync(pending.Manager);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public IReadOnlySet<string> PausedInfoHashes => _pausedHashes;

    public Task StartAsync(string id) => RunOperationAsync(id, "Starting…", async manager =>
    {
        _completionErrors.TryRemove(id, out _);
        _pausedHashes.Remove(DescribeInfoHashes(manager.InfoHashes));
        await manager.StartAsync();
    });

    public Task PauseAsync(string id) => RunOperationAsync(id, "Stopping…", async manager =>
    {
        _pausedHashes.Add(DescribeInfoHashes(manager.InfoHashes));
        await manager.StopAsync(StopAnnounceTimeout);
    });

    public Task RecheckAsync(string id) => RunOperationAsync(id, "Rechecking…", async manager =>
    {
        _completionErrors.TryRemove(id, out _);
        _pausedHashes.Remove(DescribeInfoHashes(manager.InfoHashes));
        await manager.StopAsync(StopAnnounceTimeout);
        await manager.HashCheckAsync(autoStart: true);
    });

    public Task RemoveAsync(string id) => RunOperationAsync(id, "Stopping…", manager => RemoveCoreAsync(id, manager));

    private async Task RunOperationAsync(string id, string operation, Func<TorrentManager, Task> action)
    {
        if (!_operations.TryAdd(id, operation))
        {
            throw new InvalidOperationException("An operation is already in progress for this torrent.");
        }

        try
        {
            TorrentManager manager;
            await _gate.WaitAsync();
            try { manager = Find(id); }
            finally { _gate.Release(); }
            await action(manager);
            await _gate.WaitAsync();
            try
            {
                if (_entries.ContainsKey(id) && File.Exists(_paths.EngineStateFile))
                {
                    // Persist intent without forcing fast-resume writes during Starting / Hashing.
                    var state = BEncodedValue.Decode<BEncodedDictionary>(await File.ReadAllBytesAsync(_paths.EngineStateFile));
                    await WriteStateAsync(state);
                }
            }
            finally { _gate.Release(); }
        }
        finally
        {
            _operations.TryRemove(id, out _);
        }
    }

    private async Task RemoveCoreAsync(string id, TorrentManager manager)
    {
        await manager.StopAsync(StopAnnounceTimeout);
        await _gate.WaitAsync();
        try
        {
            var engine = RequireEngine();
            if (!_entries.TryGetValue(id, out var entry))
            {
                return;
            }

            // Persist the intended queue first: a disk failure must leave a visible, retryable torrent.
            await SaveStateAsync(id);
            await engine.RemoveAsync(entry.Manager);
            _entries.Remove(id);
            _pausedHashes.Remove(DescribeInfoHashes(entry.Manager.InfoHashes));
            _completionErrors.TryRemove(id, out _);
            _lastReannounce.TryRemove(id, out _);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ApplySettingsAsync(AppSettings settings)
    {
        await _gate.WaitAsync();
        try
        {
            settings.Normalize();
            _settings = settings.Copy();
            if (_engine is not null)
            {
                await _engine.UpdateSettingsAsync(CreateEngineSettings(_settings));
                foreach (var manager in _engine.Torrents)
                {
                    await manager.UpdateSettingsAsync(CreateTorrentSettings(_settings));
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<TorrentSnapshot>> GetSnapshotsAsync(IReadOnlySet<string>? deferredCompletionIds = null)
    {
        KeyValuePair<string, (TorrentManager Manager, string Destination)>[] entries;
        await _gate.WaitAsync();
        try { entries = _entries.ToArray(); }
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

            if (manager.State == TorrentState.Downloading)
            {
                var now = DateTimeOffset.UtcNow;
                var last = _lastReannounce.GetOrAdd(pair.Key, now);
                if (ShouldReannounceStarvedTorrent(manager.State, manager.OpenConnections, now - last) &&
                    _lastReannounce.TryUpdate(pair.Key, now, last))
                {
                    _ = ReannounceStarvedTorrentAsync(manager);
                }
            }
            else
            {
                _lastReannounce.TryRemove(pair.Key, out _);
            }

            var monitor = manager.Monitor;
            var (selectedBytes, verifiedBytes) = GetSelectedBytes(manager);
            var downloaded = monitor.DataBytesReceived;
            var uploaded = monitor.DataBytesSent;
            var diagnostics = await GetDiagnosticsAsync(pair.Key, manager);
            snapshots.Add(new TorrentSnapshot(
                pair.Key,
                string.IsNullOrWhiteSpace(manager.Name) ? "Fetching metadata…" : manager.Name,
                pair.Value.Destination,
                manager.State.ToString(),
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
                diagnostics));
        }

        UpdateSystemSleepAssertion(snapshots.Any(s => s.State is "Downloading" or "Metadata" or "Hashing" or "Starting"));
        return snapshots;
    }

    public static bool ShouldReannounceStarvedTorrent(TorrentState state, int openConnections, TimeSpan elapsedSinceLastAnnounce) =>
        state == TorrentState.Downloading &&
        openConnections < EnginePerformanceTuning.LowPeerThreshold &&
        elapsedSinceLastAnnounce >= EnginePerformanceTuning.ReannounceInterval;

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

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = false)]
    private static extern uint SetThreadExecutionState(uint esFlags);
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;

    private void UpdateSystemSleepAssertion(bool hasActiveTransfers)
    {
        if (_keepingSystemAwake == hasActiveTransfers || !OperatingSystem.IsWindows())
        {
            return;
        }

        _keepingSystemAwake = hasActiveTransfers;
        try
        {
            _ = SetThreadExecutionState(hasActiveTransfers ? EsContinuous | EsSystemRequired : EsContinuous);
        }
        catch
        {
            // Non-critical power hint.
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
        double? coverage = null;
        string? peerError = null;
        try
        {
            var peers = (await manager.GetPeersAsync()).Where(peer => peer.IsConnected).ToArray();
            connected = peers.Length;
            known += connected;
            receiving = peers.Count(peer => peer.Monitor.DownloadRate > 0);
            var pieces = manager.HasMetadata ? new BitField(manager.Bitfield.Length).Or(manager.Bitfield) : null;
            foreach (var peer in peers)
            {
                if (peer.IsSeeder) seeders++;
                if (pieces is not null && peer.BitField.Length == pieces.Length) pieces.Or(peer.BitField);
            }
            coverage = pieces?.PercentComplete;
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
            EffectiveLimit(engine.Settings.MaximumUploadRate, manager.Settings.MaximumUploadRate), trackers, peerError, error);
    }

    public static long EffectiveLimit(long global, long torrent) => global <= 0 ? torrent : torrent <= 0 ? global : Math.Min(global, torrent);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        UpdateSystemSleepAssertion(false);
        await Task.WhenAll(_automaticRemovals.Values);
        await _gate.WaitAsync();
        try
        {
            if (_engine is not null)
            {
                foreach (var pending in _pending.Values)
                {
                    await _engine.RemoveAsync(pending.Manager);
                }

                _pending.Clear();
                try
                {
                    await _engine.StopAllAsync(TimeSpan.FromSeconds(2));
                    await SaveStateAsync();
                }
                finally
                {
                    _engine.Dispose();
                    _engine = null;
                }
            }
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    // Caller holds _gate so queue changes cannot race the snapshot or file replacement.
    private async Task SaveStateAsync(string? excludedId = null)
    {
        byte[] buffer;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                buffer = await RequireEngine().SaveStateAsync();
                break;
            }
            catch (IOException exception) when ((exception.HResult & 0xffff) is 32 or 33 && attempt < 4)
            {
                // MonoTorrent can still be writing fast-resume data after a state transition.
                await Task.Delay(100);
            }
        }
        var state = BEncodedValue.Decode<BEncodedDictionary>(buffer);
        var committed = _entries.Where(entry => entry.Key != excludedId).Select(entry => entry.Value.Manager.MagnetLink.ToV1String()).ToHashSet();
        var torrents = (BEncodedList)state[nameof(ClientEngine.Torrents)];
        for (var index = torrents.Count - 1; index >= 0; index--)
        {
            if (!committed.Contains(((BEncodedString)((BEncodedDictionary)torrents[index])[nameof(TorrentManager.MagnetLink)]).Text))
            {
                torrents.RemoveAt(index);
            }
        }

        var excludedHash = excludedId is not null && _entries.TryGetValue(excludedId, out var excluded)
            ? DescribeInfoHashes(excluded.Manager.InfoHashes) : null;
        await WriteStateAsync(state, excludedHash);
    }

    private async Task WriteStateAsync(BEncodedDictionary state, string? excludedHash = null)
    {
        state["PausedInfoHashes"] = new BEncodedList(_pausedHashes.Where(hash => hash != excludedHash).Select(hash => (BEncodedValue)new BEncodedString(hash)));
        var temporaryFile = $"{_paths.EngineStateFile}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporaryFile, state.Encode());
            File.Move(temporaryFile, _paths.EngineStateFile, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryFile))
            {
                File.Delete(temporaryFile);
            }
        }
    }

    private EngineSettings CreateEngineSettings(AppSettings settings)
    {
        var listenAddress = NetworkBinding.ResolveListenAddress(settings.ListenAddress);
        var builder = new EngineSettingsBuilder
        {
            CacheDirectory = _paths.CacheDirectory,
            AutoSaveLoadDhtCache = true,
            AutoSaveLoadFastResume = true,
            AutoSaveLoadMagnetLinkMetadata = true,
            AllowLocalPeerDiscovery = settings.EnableLocalPeerDiscovery,
            AllowPortForwarding = settings.EnablePortForwarding,
            AllowedEncryption = EnginePerformanceTuning.GetAllowedEncryption(settings.RequireEncryptedPeerConnections).ToList(),
            ConnectionTimeout = EnginePerformanceTuning.ConnectionTimeout,
            DhtEndPoint = settings.EnableDht ? new IPEndPoint(listenAddress, settings.ListenPort) : null,
            ListenEndPoints = new Dictionary<string, IPEndPoint>
            {
                ["ipv4"] = new(listenAddress, settings.ListenPort)
            },
            MaximumDownloadRate = ToBytesPerSecond(settings.MaximumDownloadKiBPerSecond),
            MaximumUploadRate = ToBytesPerSecond(settings.MaximumUploadKiBPerSecond),
            MaximumConnections = EnginePerformanceTuning.MaxConnections,
            MaximumHalfOpenConnections = EnginePerformanceTuning.MaxHalfOpenConnections,
            DiskCacheBytes = EnginePerformanceTuning.DiskCacheBytes,
            WebSeedDelay = EnginePerformanceTuning.WebSeedDelay
        };
        return builder.ToSettings();
    }

    private static TorrentSettings CreateTorrentSettings(AppSettings settings) => new TorrentSettingsBuilder
    {
        AllowDht = settings.EnableDht,
        AllowPeerExchange = settings.EnablePeerExchange,
        CreateContainingDirectory = true,
        MaximumConnections = EnginePerformanceTuning.MaxConnectionsPerTorrent,
        MaximumDownloadRate = ToBytesPerSecond(settings.MaximumDownloadKiBPerSecond),
        MaximumUploadRate = ToBytesPerSecond(settings.MaximumUploadKiBPerSecond),
        UploadSlots = EnginePerformanceTuning.UploadSlots
    }.ToSettings();

    private static int ToBytesPerSecond(int kibPerSecond) =>
        kibPerSecond <= 0 ? 0 : checked(kibPerSecond * 1024);

    private static bool IsRecoverableStateException(Exception exception) =>
        exception is IOException or InvalidDataException or ArgumentException or System.Text.Json.JsonException;

    private void Track(TorrentManager manager, string destination)
    {
        var id = Guid.NewGuid().ToString("N");
        _entries[id] = (manager, destination);
    }

    private TorrentManager Find(string id) =>
        _entries.TryGetValue(id, out var entry)
            ? entry.Manager
            : throw new KeyNotFoundException("That torrent is no longer in the queue.");

    private ClientEngine RequireEngine() => _engine ?? throw new InvalidOperationException("Torrent engine has not started.");

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(TorrentEngineService));
        }
    }
}
