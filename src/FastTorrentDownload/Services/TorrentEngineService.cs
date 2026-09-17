using System.Diagnostics;
using System.Net;
using FastTorrentDownload.Models;
using MonoTorrent;
using MonoTorrent.Client;
using MonoTorrent.Connections;

namespace FastTorrentDownload.Services;

public sealed class TorrentEngineService : IAsyncDisposable
{
    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (TorrentManager Manager, string Destination)> _entries = new();
    private readonly Dictionary<string, (TorrentManager Manager, string Destination)> _pending = new();
    private ClientEngine? _engine;
    private AppSettings _settings = new();
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
            AppLogger.Log($"Engine initialized: listen={_settings.ListenAddress}:{_settings.ListenPort} dht={_settings.EnableDht} pex={_settings.EnablePeerExchange} lpd={_settings.EnableLocalPeerDiscovery} upnp={_settings.EnablePortForwarding} restored={_engine.Torrents.Count} dhtState={_engine.Dht.State} dhtNodes={_engine.Dht.NodeCount}");

            foreach (var manager in _engine.Torrents)
            {
                Track(manager, "Restored download");
            }
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

        var stopwatch = Stopwatch.StartNew();
        var kind = source.TrimStart().StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) ? "magnet" : "file";
        AppLogger.Log($"PrepareAdd start kind={kind} hash={manager.InfoHashes} dest={safeDestination} dhtState={engine.Dht.State} dhtNodes={engine.Dht.NodeCount}");
        EventHandler<PeersAddedEventArgs> peersHandler = (_, e) => AppLogger.Log($"PrepareAdd: peers found new={e.NewPeers} known={e.ExistingPeers}");
        EventHandler<TorrentStateChangedEventArgs> stateHandler = (_, e) => AppLogger.Log($"PrepareAdd: pending state {e.OldState} -> {e.NewState}");
        manager.PeersFound += peersHandler;
        manager.TorrentStateChanged += stateHandler;
        try
        {
            if (!manager.HasMetadata)
            {
                await manager.StartAsync();
                try
                {
                    // No fixed timeout: thin swarms can take several minutes of DHT
                    // sampling before a metadata holder is found (uTorrent waits
                    // indefinitely too). The caller cancels via the token — the file
                    // picker wires its Cancel button and window close to it.
                    using var snapshots = new CancellationTokenSource();
                    var snapshotTask = SnapshotFetchAsync(manager, engine, stopwatch, snapshots.Token);
                    try
                    {
                        await manager.WaitForMetadataAsync(cancellationToken);
                    }
                    finally
                    {
                        snapshots.Cancel();
                        try
                        {
                            await snapshotTask;
                        }
                        catch (OperationCanceledException)
                        {
                            // Expected when the fetch finishes or is cancelled.
                        }
                    }
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

            AppLogger.Log($"PrepareAdd done in {stopwatch.Elapsed:mm\\:ss} files={manager.Files.Count}");
            return new TorrentAddPreview(
                id,
                string.IsNullOrWhiteSpace(manager.Name) ? "Torrent" : manager.Name,
                manager.Files.Select(file => new TorrentFilePreview(file.Path, file.Length)).ToArray());
        }
        catch (Exception exception)
        {
            AppLogger.LogException($"PrepareAdd failed after {stopwatch.Elapsed:mm\\:ss}", exception);
            await engine!.RemoveAsync(manager);
            throw;
        }
        finally
        {
            manager.PeersFound -= peersHandler;
            manager.TorrentStateChanged -= stateHandler;
        }
    }

    private static async Task SnapshotFetchAsync(TorrentManager manager, ClientEngine engine, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            var detail = "peers=?";
            try
            {
                var peers = await manager.GetPeersAsync();
                var shown = peers.Take(5).Select(peer =>
                    $"{peer.Uri.Host}:{peer.Uri.Port} {peer.ClientApp} seeder={peer.IsSeeder} lt={peer.SupportsLTMessages} dir={peer.ConnectionDirection} enc={peer.EncryptionType}");
                detail = $"peers={peers.Count} open={manager.OpenConnections} [{string.Join("; ", shown)}]";
            }
            catch
            {
                // A snapshot must never break the fetch.
            }

            AppLogger.Log($"fetch t+{stopwatch.Elapsed:mm\\:ss} metadata={manager.HasMetadata} state={manager.State} {detail} dht={engine.Dht.State} nodes={engine.Dht.NodeCount}");
        }
    }

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
            if (startImmediately)
            {
                await pending.Manager.StartAsync();
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

    public async Task StartAsync(string id, CancellationToken cancellationToken = default)
    {
        var manager = Find(id);
        await manager.StartAsync();
    }

    public async Task PauseAsync(string id, CancellationToken cancellationToken = default)
    {
        var manager = Find(id);
        await manager.StopAsync();
    }

    public async Task RecheckAsync(string id, CancellationToken cancellationToken = default)
    {
        var manager = Find(id);
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
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public IReadOnlyList<TorrentSnapshot> GetSnapshots()
    {
        return _entries.Select(pair =>
        {
            var manager = pair.Value.Manager;
            var monitor = manager.Monitor;
            var downloaded = monitor.DataBytesReceived;
            var uploaded = monitor.DataBytesSent;
            return new TorrentSnapshot(
                pair.Key,
                string.IsNullOrWhiteSpace(manager.Name) ? "Fetching metadata…" : manager.Name,
                pair.Value.Destination,
                manager.State.ToString(),
                manager.Progress,
                monitor.DownloadRate,
                monitor.UploadRate,
                downloaded,
                uploaded,
                manager.Progress >= 100);
        }).ToArray();
    }

    public async Task EnforceRatioPolicyAsync(CancellationToken cancellationToken = default)
    {
        foreach (var snapshot in GetSnapshots().Where(snapshot => QueueCoordinator.ShouldPauseForRatio(snapshot, _settings)))
        {
            await PauseAsync(snapshot.Id, cancellationToken);
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
            MaximumUploadRate = ToBytesPerSecond(settings.MaximumUploadKiBPerSecond)
        };
        return builder.ToSettings();
    }

    private static TorrentSettings CreateTorrentSettings(AppSettings settings) => new TorrentSettingsBuilder
    {
        AllowDht = settings.EnableDht,
        AllowPeerExchange = settings.EnablePeerExchange,
        CreateContainingDirectory = true,
        MaximumDownloadRate = ToBytesPerSecond(settings.MaximumDownloadKiBPerSecond),
        MaximumUploadRate = ToBytesPerSecond(settings.MaximumUploadKiBPerSecond)
    }.ToSettings();

    private static int ToBytesPerSecond(int kibPerSecond) =>
        kibPerSecond <= 0 ? 0 : checked(kibPerSecond * 1024);

    private static bool IsRecoverableStateException(Exception exception) =>
        exception is IOException or InvalidDataException or ArgumentException or System.Text.Json.JsonException;

    private string Track(TorrentManager manager, string destination)
    {
        var id = Guid.NewGuid().ToString("N");
        _entries[id] = (manager, destination);
        return id;
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
