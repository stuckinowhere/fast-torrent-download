using System.Net;
using FastTorrentDownload.Models;
using MonoTorrent;
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
    private bool _disposed;

    public TorrentEngineService(AppPaths paths) => _paths = paths;

    public async Task InitializeAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
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
            await DhtBootstrapCache.EnsureSeededAsync(_paths.CacheDirectory, cancellationToken);
            if (File.Exists(_paths.EngineStateFile))
            {
                try
                {
                    _engine = await ClientEngine.RestoreStateAsync(_paths.EngineStateFile);
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
                Track(manager, "Restored download");
                await manager.UpdateSettingsAsync(CreateTorrentSettings(_settings));
            }

            await _engine.StartAllAsync();
            var repaused = 0;
            foreach (var manager in _engine.Torrents)
            {
                if (_pausedHashes.Contains(DescribeInfoHashes(manager.InfoHashes)))
                {
                    await manager.StopAsync();
                    repaused++;
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
        new("https://opentracker.io/announce"),
        new("udp://open.stealth.si:80/announce"),
        new("https://torrent.eu.org/announce.php"),
        new("udp://exodus.desync.com:6969/announce"),
    ];

    private static async Task SeedDefaultTrackersAsync(TorrentManager manager)
    {
        if (CountTrackers(manager) > 0)
        {
            return;
        }

        foreach (var uri in DefaultPublicTrackers)
        {
            try
            {
                await manager.TrackerManager.AddTrackerAsync(uri);
            }
            catch (Exception exception)
            {
                // A bad default entry must never break the add; DHT remains.
                AppLogger.Log($"Default tracker: skipping {uri} ({exception.GetType().Name}).");
            }
        }

        AppLogger.Log($"Default tracker: seeded {CountTrackers(manager)} public trackers for trackerless magnet.");
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
        bool startImmediately,
        CancellationToken cancellationToken = default)
    {
        if (selectedFiles.Count == 0)
        {
            throw new ArgumentException("Select at least one file to download.", nameof(selectedFiles));
        }

        await _gate.WaitAsync(cancellationToken);
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
            if (startImmediately)
            {
                await pending.Manager.StartAsync();
            }
            else
            {
                _pausedHashes.Add(hash);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CancelAddAsync(string id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
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

    public async Task StartAsync(string id)
    {
        var manager = Find(id);
        _pausedHashes.Remove(DescribeInfoHashes(manager.InfoHashes));
        await manager.StartAsync();
    }

    public async Task PauseAsync(string id)
    {
        var manager = Find(id);
        _pausedHashes.Add(DescribeInfoHashes(manager.InfoHashes));
        await manager.StopAsync();
    }

    public async Task RecheckAsync(string id)
    {
        var manager = Find(id);
        _pausedHashes.Remove(DescribeInfoHashes(manager.InfoHashes));
        await manager.HashCheckAsync(autoStart: true);
    }

    public async Task RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var engine = RequireEngine();
            if (!_entries.Remove(id, out var entry))
            {
                return;
            }

            _pausedHashes.Remove(DescribeInfoHashes(entry.Manager.InfoHashes));
            await engine.RemoveAsync(entry.Manager);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ApplySettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
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

    public async Task<IReadOnlyList<TorrentSnapshot>> GetSnapshotsAsync(bool includePeerCounts = true)
    {
        var snapshots = new List<TorrentSnapshot>(_entries.Count);
        foreach (var pair in _entries.ToArray())
        {
            var manager = pair.Value.Manager;
            var monitor = manager.Monitor;
            var downloaded = monitor.DataBytesReceived;
            var uploaded = monitor.DataBytesSent;
            var (seeders, leechers) = includePeerCounts ? await CountPeersAsync(manager) : (0, 0);
            snapshots.Add(new TorrentSnapshot(
                pair.Key,
                string.IsNullOrWhiteSpace(manager.Name) ? "Fetching metadata…" : manager.Name,
                pair.Value.Destination,
                manager.State.ToString(),
                manager.Progress,
                monitor.DownloadRate,
                monitor.UploadRate,
                downloaded,
                uploaded,
                manager.Progress >= 100,
                seeders,
                leechers));
        }
        return snapshots;
    }

    private static async Task<(int Seeders, int Leechers)> CountPeersAsync(TorrentManager manager)
    {
        try
        {
            var peers = await manager.GetPeersAsync();
            var seeders = 0;
            foreach (var peer in peers)
            {
                if (peer.IsSeeder)
                {
                    seeders++;
                }
            }

            return (seeders, peers.Count - seeders);
        }
        catch
        {
            return (0, 0);
        }
    }

    public async Task EnforceRatioPolicyAsync()
    {
        foreach (var snapshot in (await GetSnapshotsAsync(includePeerCounts: false)).Where(snapshot => QueueCoordinator.ShouldPauseForRatio(snapshot, _settings)))
        {
            await PauseAsync(snapshot.Id);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
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
                    await _engine.SaveStateAsync(_paths.EngineStateFile);
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
            AllowedEncryption = settings.RequireEncryptedPeerConnections
                ? [EncryptionType.RC4Header, EncryptionType.RC4Full]
                : [EncryptionType.PlainText, EncryptionType.RC4Header, EncryptionType.RC4Full],
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
        MaximumUploadRate = ToBytesPerSecond(settings.MaximumUploadKiBPerSecond)
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
