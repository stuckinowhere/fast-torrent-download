using System.Net;
using System.Collections.Concurrent;
using FastTorrentDownload.Models;
using MonoTorrent;
using MonoTorrent.BEncoding;
using MonoTorrent.Client;
using MonoTorrent.Connections;
using MonoTorrent.Trackers;

namespace FastTorrentDownload.Services;

public sealed partial class TorrentEngineService : IAsyncDisposable
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
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastScrape = new();
    // Last payload byte count seen per torrent and when it last changed; drives stall detection.
    private readonly ConcurrentDictionary<string, (long Received, DateTimeOffset At)> _lastPayload = new();
    private readonly ConcurrentDictionary<string, byte> _stalledIds = new();
    private readonly ConcurrentQueue<TorrentCompletion> _completions = new();
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, TorrentDiagnostics Value)> _diagnosticsCache = new();
    // Torrents that want to run but wait for a free slot under MaximumActiveDownloads.
    private readonly HashSet<string> _queuedHashes = new(StringComparer.OrdinalIgnoreCase);
    // Trackers added after the torrent was created (public defaults and user additions), keyed by
    // info-hash. MonoTorrent restores only the magnet's own trackers, so these are persisted here.
    private readonly Dictionary<string, HashSet<string>> _extraTrackers = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _restoreWarnings = [];
    private bool _stateRecordsTrackers;
    public const int MaxExtraTrackersPerTorrent = 50;
    public static TimeSpan StopAnnounceTimeout { get; } = TimeSpan.FromSeconds(2);
    private const int MaxCachedTorrentBytes = 10 * 1024 * 1024;
    // Peer enumeration and piece-coverage math run per torrent; refreshing them every second
    // is wasted CPU for a panel that is usually collapsed.
    public static TimeSpan DiagnosticsInterval { get; } = TimeSpan.FromSeconds(3);
    // A torrent that receives no payload for this long is reported as "Stalled" instead of "Downloading".
    // Settable so the checks can exercise the transition without waiting minutes.
    public static TimeSpan StallThreshold { get; set; } = TimeSpan.FromMinutes(5);
    public static TimeSpan ScrapeInterval { get; } = TimeSpan.FromMinutes(5);
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
                        _pausedHashes.UnionWith(hashes.OfType<BEncodedString>().Select(hash => hash.Text));
                    }

                    _stateRecordsTrackers = saved.ContainsKey("ExtraTrackers");
                    if (saved.TryGetValue("ExtraTrackers", out var extra) && extra is BEncodedDictionary byHash)
                    {
                        foreach (var pair in byHash)
                        {
                            if (pair.Value is BEncodedList urls)
                            {
                                _extraTrackers[pair.Key.Text] = urls.OfType<BEncodedString>().Select(url => url.Text)
                                    .Take(MaxExtraTrackersPerTorrent).ToHashSet(StringComparer.OrdinalIgnoreCase);
                            }
                        }
                    }
                }
                catch (Exception exception) when (IsRecoverableStateException(exception))
                {
                    // A corrupt or incompatible state file must never prevent the client from opening.
                    // Keep a copy: the next save would otherwise overwrite the only record of the queue.
                    AppLogger.LogException("Saved queue could not be restored", exception);
                    if (_engine is null)
                    {
                        _restoreWarnings.Add("The saved queue could not be read and was set aside as engine-state.json.corrupt. The queue starts empty; downloaded files are untouched.");
                        try { File.Copy(_paths.EngineStateFile, _paths.EngineStateFile + ".corrupt", overwrite: true); }
                        catch (Exception copyFailure) when (copyFailure is IOException or UnauthorizedAccessException) { }
                    }
                }
            }

            _engine ??= new ClientEngine(CreateEngineSettings(_settings));
            await _engine.UpdateSettingsAsync(CreateEngineSettings(_settings));
            AppLogger.Log($"Engine initialized: listen={_settings.ListenAddress}:{_settings.ListenPort} dht={_settings.EnableDht} pex={_settings.EnablePeerExchange} lpd={_settings.EnableLocalPeerDiscovery} upnp={_settings.EnablePortForwarding} restored={_engine.Torrents.Count} dhtState={_engine.Dht.State} dhtNodes={_engine.Dht.NodeCount} tuning={EnginePerformanceTuning.MaxConnections}c/{EnginePerformanceTuning.MaxHalfOpenConnections}half/{EnginePerformanceTuning.MaxConnectionsPerTorrent}pt");

            foreach (var manager in _engine.Torrents.ToList())
            {
                // The state file is plain data on disk: its paths get the same containment check as a new add.
                if (!IsSafeRestoredTorrent(manager))
                {
                    var name = SanitizeDisplayName(manager.Name);
                    AppLogger.Log("Restored torrent rejected: saved file paths leave the download folder.");
                    _restoreWarnings.Add($"“{name}” was not restored: its saved file paths point outside its download folder.");
                    _extraTrackers.Remove(DescribeInfoHashes(manager.InfoHashes));
                    await _engine.RemoveAsync(manager);
                    continue;
                }

                Track(manager, manager.SavePath);
                await manager.UpdateSettingsAsync(CreateTorrentSettings(_settings));
                await UpgradeLegacyTrackersAsync(manager);
                await RestoreExtraTrackersAsync(manager);
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

            PathGuard.ThrowIfUnsafeTorrentFiles(pending.Destination, pending.Manager.Files.Select(file => file.Path).Prepend(pending.Manager.Name));
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

    public async Task CancelAddAsync(string id)
    {
        await _gate.WaitAsync();
        try
        {
            var engine = RequireEngine();
            if (_pending.Remove(id, out var pending))
            {
                _extraTrackers.Remove(DescribeInfoHashes(pending.Manager.InfoHashes));
                await engine.RemoveAsync(pending.Manager);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public IReadOnlySet<string> PausedInfoHashes => _pausedHashes;

    /// <summary>Problems found while restoring the saved queue; shown once after startup.</summary>
    public IReadOnlyList<string> RestoreWarnings => _restoreWarnings;

    private static bool IsSafeRestoredTorrent(TorrentManager manager) =>
        manager.Files.Count == 0 ||
        manager.Files.Select(file => file.Path).Prepend(manager.Name).All(path => PathGuard.IsSafeTorrentFilePath(manager.SavePath, path));

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

    public Task RemoveAsync(string id, bool deleteFiles = false) =>
        RunOperationAsync(id, "Stopping…", manager => RemoveCoreAsync(id, manager, deleteFiles));

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
                    BEncodedDictionary? state = null;
                    try
                    {
                        state = BEncodedValue.Decode<BEncodedDictionary>(await File.ReadAllBytesAsync(_paths.EngineStateFile));
                    }
                    catch (Exception exception) when (IsRecoverableStateException(exception))
                    {
                        // An unreadable state file is rebuilt from the live engine below.
                    }

                    if (state is null) await SaveStateAsync();
                    else await WriteStateAsync(state);
                }
            }
            finally { _gate.Release(); }
        }
        finally
        {
            _operations.TryRemove(id, out _);
        }
    }

    private async Task RemoveCoreAsync(string id, TorrentManager manager, bool deleteFiles = false)
    {
        await manager.StopAsync(StopAnnounceTimeout);
        var savePath = manager.SavePath;
        var payloadFiles = manager.Files.Select(file => file.FullPath).ToArray();
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
            _extraTrackers.Remove(hash);
            _completionErrors.TryRemove(id, out _);
            _lastReannounce.TryRemove(id, out _);
            _lastScrape.TryRemove(id, out _);
            _lastPayload.TryRemove(id, out _);
            _stalledIds.TryRemove(id, out _);
            _diagnosticsCache.TryRemove(id, out _);
        }
        finally
        {
            _gate.Release();
        }

        if (deleteFiles)
        {
            var deleted = DeleteDownloadedFiles(savePath, payloadFiles);
            AppLogger.Log($"Removed torrent and deleted {deleted} of {payloadFiles.Length} payload file(s).");
        }
    }

    /// <summary>
    /// Deletes a removed torrent's payload files and the folders they leave empty. Only paths
    /// inside <paramref name="savePath"/> are touched, and the save folder itself is never removed.
    /// </summary>
    public static int DeleteDownloadedFiles(string savePath, IReadOnlyCollection<string> files)
    {
        var deleted = 0;
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (!PathGuard.IsInsideFolder(savePath, file)) continue;
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                    deleted++;
                }

                for (var folder = Path.GetDirectoryName(Path.GetFullPath(file));
                     folder is not null && PathGuard.IsInsideFolder(savePath, folder);
                     folder = Path.GetDirectoryName(folder))
                {
                    folders.Add(folder);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                AppLogger.Log($"Could not delete a payload file ({exception.GetType().Name}).");
            }
        }

        // Deepest first, so a parent is empty by the time it is tried.
        foreach (var folder in folders.OrderByDescending(folder => folder.Length))
        {
            try
            {
                if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                {
                    Directory.Delete(folder);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        return deleted;
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
