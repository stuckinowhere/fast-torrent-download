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
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, TorrentDiagnostics Value)> _diagnosticsCache = new();
    // Torrents that want to run but wait for a free slot under MaximumActiveDownloads.
    private readonly HashSet<string> _queuedHashes = new(StringComparer.OrdinalIgnoreCase);
    public static TimeSpan StopAnnounceTimeout { get; } = TimeSpan.FromSeconds(2);
    // Peer enumeration and piece-coverage math run per torrent; refreshing them every second
    // is wasted CPU for a panel that is usually collapsed.
    public static TimeSpan DiagnosticsInterval { get; } = TimeSpan.FromSeconds(3);
    private static readonly HttpClient MetadataCacheHttpClient = CreateMetadataCacheHttpClient();
    private static readonly ConcurrentDictionary<string, byte[]> CachedMagnetTorrents = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Task<Torrent?>> InFlightMagnetPrefetches = new(StringComparer.OrdinalIgnoreCase);
    private readonly SystemSleepGuard _sleepGuard = new();
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
                    await StartOrQueueAsync(manager);
                }
            }

            AppLogger.Log($"Engine started: resumed {_engine.Torrents.Count} restored torrent(s), kept {repaused} paused, queued {_queuedHashes.Count}.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public static string SanitizeDisplayName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        return string.Join(' ', name.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    public async Task<TorrentAddPreview> PrepareAddAsync(
        string source,
        string? destination,
        CancellationToken cancellationToken = default,
        Action<string, string?>? onProgress = null)
    {
        ClientEngine engine;
        string safeSource;
        string safeDestination;
        bool usePublicCaches;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            engine = RequireEngine();
            safeSource = PathGuard.ResolveTorrentSource(source);
            safeDestination = PathGuard.ResolveDestination(_settings.DefaultDownloadFolder, destination);
            usePublicCaches = _settings.UsePublicMetadataCaches;
        }
        finally
        {
            _gate.Release();
        }

        MagnetLink? magnet = null;
        Torrent torrent;
        if (safeSource.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            magnet = MagnetLink.Parse(safeSource);
            ThrowIfAlreadyQueued(engine, magnet.InfoHashes);
            torrent = usePublicCaches && TryGetVerifiedCachedTorrent(magnet, out var prefetched)
                ? prefetched!
                : await ResolveMagnetMetadataAsync(engine, magnet, usePublicCaches, cancellationToken, onProgress);
        }
        else
        {
            torrent = Torrent.Load(safeSource);
        }

        // Validate metadata before registering a manager that can write payload files.
        PathGuard.ThrowIfUnsafeTorrentFiles(safeDestination,
            torrent.Files.Select(file => file.Path).Prepend(torrent.Name));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            ThrowIfAlreadyQueued(engine, torrent.InfoHashes);
            var manager = magnet is null
                ? await engine.AddAsync(torrent, safeDestination, CreateTorrentSettings(_settings))
                : await AddTorrentFromCachedMetadataAsync(engine, magnet, torrent, safeDestination);
            var id = Guid.NewGuid().ToString("N");
            _pending[id] = (manager, safeDestination);
            var cleanName = SanitizeDisplayName(manager.Name);
            return new TorrentAddPreview(
                id,
                string.IsNullOrWhiteSpace(cleanName) ? "Torrent" : cleanName,
                manager.Files.Select(file => new TorrentFilePreview(file.Path, file.Length)).ToArray());
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void ThrowIfAlreadyQueued(ClientEngine engine, InfoHashes hashes)
    {
        if (engine.Torrents.Any(manager => manager.InfoHashes == hashes))
        {
            throw new InvalidOperationException("This torrent is already in the queue.");
        }
    }

    private static async Task<Torrent> ResolveMagnetMetadataAsync(
        ClientEngine engine, MagnetLink magnet, bool usePublicCaches, CancellationToken cancellationToken,
        Action<string, string?>? onProgress)
    {
        using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        raceCts.CancelAfter(TimeSpan.FromSeconds(60));
        var progressTask = ReportMetadataProgressAsync(onProgress, raceCts.Token);
        var swarmTask = DownloadMetadataWithTrackerRotationAsync(engine, magnet, raceCts.Token);
        // The public caches learn the info-hash of every lookup, so they are strictly opt-in.
        var cacheTask = usePublicCaches ? TryFetchCachedTorrentAsync(magnet, raceCts.Token) : Task.FromResult<Torrent?>(null);
        try
        {
            if (await Task.WhenAny((Task)swarmTask, cacheTask) == cacheTask &&
                await cacheTask is { } cached)
            {
                return cached;
            }

            return await swarmTask;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("No torrent file list was received within 60 seconds. Try again or use a .torrent file.");
        }
        finally
        {
            raceCts.Cancel();
            // Await cleanup before registering the same hash in the payload engine.
            try { await swarmTask; } catch (OperationCanceledException) { }
            try { await cacheTask; } catch (OperationCanceledException) { }
            try { await progressTask; } catch (OperationCanceledException) { }
        }
    }

    private static async Task ReportMetadataProgressAsync(Action<string, string?>? onProgress, CancellationToken token)
    {
        var elapsed = 0;
        while (!token.IsCancellationRequested)
        {
            onProgress?.Invoke($"Fetching torrent file list… ({elapsed}s / 60s)",
                "Payload files will only download after you confirm the selection.");
            await Task.Delay(1000, token);
            elapsed++;
        }
    }

    public static bool TryCacheMagnetTorrentBytes(MagnetLink magnet, byte[] torrentBytes, out Torrent? verifiedTorrent)
    {
        verifiedTorrent = null;
        var hash = magnet.InfoHashes.V1?.ToHex().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(hash) ||
            torrentBytes is not { Length: > 32 and < 10 * 1024 * 1024 } ||
            !Torrent.TryLoad(torrentBytes, out var loaded) ||
            loaded.InfoHashes != magnet.InfoHashes)
        {
            return false;
        }

        CachedMagnetTorrents[hash] = torrentBytes;
        verifiedTorrent = loaded;
        return true;
    }

    public static bool TryGetVerifiedCachedTorrent(MagnetLink magnet, out Torrent? torrent)
    {
        torrent = null;
        var hash = magnet.InfoHashes.V1?.ToHex().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(hash) ||
            !CachedMagnetTorrents.TryGetValue(hash, out var bytes) ||
            !Torrent.TryLoad(bytes, out var loaded) ||
            loaded.InfoHashes != magnet.InfoHashes)
        {
            return false;
        }

        torrent = loaded;
        return true;
    }

    public static async Task<Torrent?> TryFetchCachedTorrentAsync(MagnetLink magnet, CancellationToken cancellationToken)
    {
        if (TryGetVerifiedCachedTorrent(magnet, out var existing))
        {
            return existing;
        }

        var hash = magnet.InfoHashes.V1?.ToHex().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(hash))
        {
            return null;
        }

        var task = InFlightMagnetPrefetches.GetOrAdd(hash, _ => FetchFromPublicCachesCoreAsync(magnet, hash));
        try
        {
            return await task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private static async Task<Torrent?> FetchFromPublicCachesCoreAsync(MagnetLink magnet, string hash)
    {
        try
        {
            string[] urls =
            [
                $"https://itorrents.org/torrent/{hash}.torrent",
                $"https://torrage.info/torrent.php?h={hash}",
                $"https://btcache.me/torrent/{hash}"
            ];
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            var tasks = urls.Select(url => FetchSingleCacheUrlAsync(url, magnet, cts.Token)).ToList();
            while (tasks.Count > 0)
            {
                var finished = await Task.WhenAny(tasks);
                tasks.Remove(finished);
                var result = await finished;
                if (result is not null)
                {
                    cts.Cancel();
                    return result;
                }
            }

            return null;
        }
        finally
        {
            InFlightMagnetPrefetches.TryRemove(hash, out _);
        }
    }

    private static async Task<Torrent?> FetchSingleCacheUrlAsync(string url, MagnetLink magnet, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await MetadataCacheHttpClient.GetAsync(url, HttpCompletionOption.ResponseContentRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            return TryCacheMagnetTorrentBytes(magnet, bytes, out var verified) ? verified : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<TorrentManager> AddTorrentFromCachedMetadataAsync(
        ClientEngine engine,
        MagnetLink magnet,
        Torrent cachedTorrent,
        string safeDestination)
    {
        var manager = await engine.AddAsync(cachedTorrent, safeDestination, CreateTorrentSettings(_settings));
        if (magnet.AnnounceUrls is not null)
        {
            foreach (var url in magnet.AnnounceUrls)
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                {
                    try { await manager.TrackerManager.AddTrackerAsync(uri); } catch { }
                }
            }
        }

        await SeedDefaultTrackersAsync(manager);
        await SplitTrackerTiersAsync(manager);
        return manager;
    }

    private static async Task TriggerImmediateAnnouncesAsync(TorrentManager manager)
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
            // Initial parallel announce kick-off is best-effort.
        }
    }

    private static HttpClient CreateMetadataCacheHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Mozilla/5.0 (Windows NT 10.0; Win64; x64) FastTorrentDownload/{AppInfo.Version}");
        return client;
    }

    private static async Task<Torrent> DownloadMetadataWithTrackerRotationAsync(
        ClientEngine engine, MagnetLink magnet, CancellationToken cancellationToken)
    {
        var trackers = magnet.AnnounceUrls.Concat(DefaultPublicTrackers.Select(uri => uri.AbsoluteUri))
            .Distinct().ToArray();
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var offset = attempt % trackers.Length;
            var candidate = new MagnetLink(magnet.InfoHashes, magnet.Name,
                [trackers[offset]], magnet.Webseeds, magnet.Size);
            using var slice = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (attempt < 5)
                slice.CancelAfter(TimeSpan.FromSeconds(attempt == 0 ? 20 : 30));
            try
            {
                // This API cannot transition from metadata to payload download before validation.
                var bytes = await engine.DownloadMetadataAsync(candidate, slice.Token);
                return Torrent.Load(bytes.ToArray());
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // One tracker per attempt avoids the library randomly selecting an empty tracker.
            }
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

            PathGuard.ThrowIfUnsafeTorrentFiles(pending.Destination, pending.Manager.Files.Select(file => file.Path));
            // The folder is only created once the user confirms, so a cancelled preview leaves no trace.
            Directory.CreateDirectory(pending.Destination);
            await ApplyFileSelectionAsync(pending.Manager, selectedFiles);

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
            if (startImmediately)
            {
                await StartOrQueueAsync(pending.Manager);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task ApplyFileSelectionAsync(TorrentManager manager, IReadOnlyCollection<string> selectedFiles)
    {
        var selected = selectedFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manager.Files)
        {
            var priority = selected.Contains(file.Path) ? Priority.Normal : Priority.DoNotDownload;
            if (file.Priority != priority)
            {
                await manager.SetFilePriorityAsync(file, priority);
            }
        }
    }

    public async Task<TorrentAddPreview> GetFilesAsync(string id)
    {
        await _gate.WaitAsync();
        try
        {
            var manager = Find(id);
            return new TorrentAddPreview(
                id,
                SanitizeDisplayName(manager.Name),
                manager.Files.Select(file => new TorrentFilePreview(file.Path, file.Length, file.Priority != Priority.DoNotDownload)).ToArray());
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task SetSelectedFilesAsync(string id, IReadOnlyCollection<string> selectedFiles)
    {
        if (selectedFiles.Count == 0)
        {
            throw new ArgumentException("Select at least one file to download.", nameof(selectedFiles));
        }

        return RunOperationAsync(id, "Updating…", async manager =>
        {
            await ApplyFileSelectionAsync(manager, selectedFiles);
            // Priorities live in the engine state, not in the fast-resume data the operation epilogue preserves.
            await _gate.WaitAsync();
            try { await SaveStateAsync(); }
            finally { _gate.Release(); }
        });
    }

    public Task MoveAsync(string id, string destination) => RunOperationAsync(id, "Moving…", async manager =>
    {
        string safeDestination;
        bool resume;
        await _gate.WaitAsync();
        try
        {
            safeDestination = PathGuard.ResolveDestination(_settings.DefaultDownloadFolder, destination);
            PathGuard.ThrowIfUnsafeTorrentFiles(safeDestination, manager.Files.Select(file => file.Path).Prepend(manager.Name));
            if (string.Equals(safeDestination, manager.SavePath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var hash = DescribeInfoHashes(manager.InfoHashes);
            resume = !_pausedHashes.Contains(hash) && !_queuedHashes.Contains(hash);
            Directory.CreateDirectory(safeDestination);
        }
        finally
        {
            _gate.Release();
        }

        // MonoTorrent only relocates payload files while the torrent is stopped.
        await manager.StopAsync(StopAnnounceTimeout);
        await manager.MoveFilesAsync(safeDestination, overWriteExisting: false);
        await _gate.WaitAsync();
        try
        {
            _entries[id] = (manager, manager.SavePath);
            await SaveStateAsync();
            if (resume)
            {
                await StartOrQueueAsync(manager);
            }
        }
        finally
        {
            _gate.Release();
        }
    });

    // Caller holds _gate. Starts the torrent, or parks it when the active-download limit is reached.
    private async Task StartOrQueueAsync(TorrentManager manager)
    {
        var hash = DescribeInfoHashes(manager.InfoHashes);
        if (_settings.MaximumActiveDownloads > 0 && CountActiveDownloads() >= _settings.MaximumActiveDownloads)
        {
            _queuedHashes.Add(hash);
            return;
        }

        _queuedHashes.Remove(hash);
        await manager.StartAsync();
        _ = TriggerImmediateAnnouncesAsync(manager);
    }

    // Caller holds _gate. Fills free slots in queue order.
    private async Task PromoteQueuedAsync()
    {
        if (_queuedHashes.Count == 0)
        {
            return;
        }

        foreach (var entry in _entries.Values)
        {
            if (!_queuedHashes.Contains(DescribeInfoHashes(entry.Manager.InfoHashes)))
            {
                continue;
            }

            if (_settings.MaximumActiveDownloads > 0 && CountActiveDownloads() >= _settings.MaximumActiveDownloads)
            {
                return;
            }

            try
            {
                await StartOrQueueAsync(entry.Manager);
            }
            catch (Exception exception)
            {
                AppLogger.LogException("Queued torrent failed to start", exception);
            }
        }
    }

    private int CountActiveDownloads() =>
        _entries.Values.Count(entry => IsActiveState(entry.Manager.State));

    public static bool IsActiveState(TorrentState state) =>
        state is TorrentState.Downloading or TorrentState.Metadata or TorrentState.Hashing
            or TorrentState.Starting or TorrentState.Seeding or TorrentState.FetchingHashes;

    public static bool ShouldQueue(int maximumActiveDownloads, int activeDownloads) =>
        maximumActiveDownloads > 0 && activeDownloads >= maximumActiveDownloads;

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
        await _gate.WaitAsync();
        try
        {
            _pausedHashes.Remove(DescribeInfoHashes(manager.InfoHashes));
            await StartOrQueueAsync(manager);
        }
        finally { _gate.Release(); }
    });

    public Task PauseAsync(string id) => RunOperationAsync(id, "Stopping…", async manager =>
    {
        await SetRunIntentAsync(manager, paused: true);
        await manager.StopAsync(StopAnnounceTimeout);
    });

    public Task RecheckAsync(string id) => RunOperationAsync(id, "Rechecking…", async manager =>
    {
        _completionErrors.TryRemove(id, out _);
        await SetRunIntentAsync(manager, paused: false);
        await manager.StopAsync(StopAnnounceTimeout);
        await manager.HashCheckAsync(autoStart: true);
    });

    private async Task SetRunIntentAsync(TorrentManager manager, bool paused)
    {
        var hash = DescribeInfoHashes(manager.InfoHashes);
        await _gate.WaitAsync();
        try
        {
            _queuedHashes.Remove(hash);
            if (paused) _pausedHashes.Add(hash);
            else _pausedHashes.Remove(hash);
        }
        finally { _gate.Release(); }
    }

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
            var hash = DescribeInfoHashes(entry.Manager.InfoHashes);
            _pausedHashes.Remove(hash);
            _queuedHashes.Remove(hash);
            _completionErrors.TryRemove(id, out _);
            _lastReannounce.TryRemove(id, out _);
            _diagnosticsCache.TryRemove(id, out _);
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
            var diagnostics = await GetCachedDiagnosticsAsync(pair.Key, manager);
            var state = manager.State == TorrentState.Stopped && queued.Contains(DescribeInfoHashes(manager.InfoHashes))
                ? "Queued"
                : manager.State.ToString();
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
                diagnostics));
        }

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

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _sleepGuard.Dispose();
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
