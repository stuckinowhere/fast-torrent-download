using FastTorrentDownload.Models;
using FastTorrentDownload.Services;
using FastTorrentDownload.ViewModels;
using System.Linq;
using System.Net;
using MonoTorrent.Client;
using MonoTorrent;
using MonoTorrent.BEncoding;
using System.Reflection;
using System.Security.Cryptography;
using System.Net.Sockets;
using System.Diagnostics;

if (args.Contains("--ui-smoke") || args.Contains("--ui-smoke-auto")) return UiSmoke.Run(args.Contains("--ui-smoke-auto"));

var passed = 0;

Check("completed selected files are removed only in a verified download state", () =>
{
    Require(TorrentEngineService.ShouldRemoveCompletedTorrent(TorrentState.Seeding, 100));
    Require(TorrentEngineService.ShouldRemoveCompletedTorrent(TorrentState.Downloading, 100));
    Require(!TorrentEngineService.ShouldRemoveCompletedTorrent(TorrentState.Downloading, 99.99));
    Require(!TorrentEngineService.ShouldRemoveCompletedTorrent(TorrentState.Hashing, 100));
    Require(!TorrentEngineService.ShouldRemoveCompletedTorrent(TorrentState.Stopped, 100));
    Require(!TorrentEngineService.ShouldRemoveCompletedTorrent(TorrentState.Error, 100));
    Require(!TorrentEngineService.ShouldRemoveCompletedTorrent(TorrentState.Metadata, 100));
});

Check("binary rate formatting preserves fractional units", () =>
{
    Require(TransferFormatting.Rate(1536) == $"{1.5:0.0} KiB/s");
    Require(TransferFormatting.Bytes(-1) == "0 B");
    Require(TransferFormatting.Bytes(1_572_864) == $"{1.5:0.0} MiB");
});

Check("ETA is based on selected bytes and stays unknown without useful speed", () =>
{
    Require(TransferFormatting.Remaining(1024, 2048, 512, "Downloading") == "ETA 0m 2s");
    Require(TransferFormatting.Remaining(1024, 2048, 0, "Downloading") == "ETA unknown");
    Require(TransferFormatting.Remaining(1024, 2048, 512, "Stopped") == "ETA unknown");
    Require(TransferFormatting.Remaining(2048, 2048, 0, "Stopped") == "Complete");
});

Check("legacy ratio settings load without overriding automatic completion", () =>
{
    var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"SeedRatioTarget\":0,\"ListenPort\":51413}");
    Require(settings?.ListenPort == 51413);
    Require(TorrentEngineService.ShouldRemoveCompletedTorrent(TorrentState.Seeding, 100));
});

Check("row operation disables actions and reports stopping without losing progress", () =>
{
    var row = new TorrentRowViewModel(Snapshot(50, 5, 0) with { SelectedBytes = 2048, VerifiedBytes = 1024 });
    row.Operation = "Stopping…";
    Require(row.IsBusy && !row.CanAct && row.StateLabel == "Stopping…" && row.Progress == 50);
    row.Operation = null;
    Require(row.CanAct && row.DetailLabel.Contains($"{1.0:0.0} KiB / {2.0:0.0} KiB verified"));
});

Check("diagnostics distinguish missing peers, idle peers, limits and unavailable statistics", () =>
{
    var data = new TorrentDiagnostics(0, 0, 0, 0, 50, "Ready", 20, 0, 0,
        [new TrackerDiagnostic("http://tracker.test", "Offline", "Timed out")]);
    Require(data.Diagnosis(0).StartsWith("No known peers"));
    Require((data with { KnownPeers = 10 }).Diagnosis(0).Contains("none are connected"));
    Require((data with { ConnectedPeers = 2 }).Diagnosis(0).Contains("none are currently sending"));
    Require((data with { ConnectedPeers = 2, ReceivingPeers = 1, DownloadLimit = 1000 }).Diagnosis(950).Contains("configured limit"));
    Require((data with { PeerError = "unavailable" }).Describe(0).Contains("Peer statistics unavailable"));
    Require(data.Describe(0).Contains("tracker.test · Offline · Timed out"));
    Require(TorrentEngineService.EffectiveLimit(0, 512) == 512);
    Require(TorrentEngineService.EffectiveLimit(1000, 512) == 512);
    Require(TorrentEngineService.EffectiveLimit(1000, 0) == 1000);
});

Check("listener binding accepts IPv4 and rejects ambiguous inputs", () =>
{
    Require(NetworkBinding.ResolveListenAddress("0.0.0.0").Equals(IPAddress.Any));
    Require(NetworkBinding.ResolveListenAddress("192.168.1.25").Equals(IPAddress.Parse("192.168.1.25")));
    try
    {
        NetworkBinding.ResolveListenAddress("not-an-ip");
        throw new InvalidOperationException("Expected invalid address to be rejected.");
    }
    catch (ArgumentException)
    {
        // Expected: settings cannot silently turn an invalid address into an all-interface bind.
    }
});

Check("magnet links with query parameters are accepted as torrent sources", () =>
{
    const string magnet = "magnet:?xt=urn:btih:792b9fa5793e46c51db29f59c6a9037cf20492f4&dn=Torrentio%0A1080p";
    Require(PathGuard.ResolveTorrentSource(magnet) == magnet);
    Require(PathGuard.ResolveTorrentSource("  MAGNET:?xt=urn:btih:abc123  ") == "MAGNET:?xt=urn:btih:abc123");
});

Check("non-torrent paths are rejected as torrent sources", () =>
{
    try
    {
        PathGuard.ResolveTorrentSource("notes.txt");
        throw new InvalidOperationException("Expected a non-torrent path to be rejected.");
    }
    catch (ArgumentException)
    {
        // Expected: only magnet links and existing .torrent files are accepted.
    }
});

Check("dht bootstrap nodes use flat 26-byte compact layout", () =>
{
    var nodeId = Enumerable.Range(1, 20).Select(i => (byte)i).ToArray();
    var compact = DhtBootstrapCache.ToCompactNode(IPAddress.Parse("1.2.3.4"), 6881, nodeId);
    Require(compact.Length == 26);
    Require(compact.Take(20).SequenceEqual(nodeId));
    Require(compact[20] == 1 && compact[21] == 2 && compact[22] == 3 && compact[23] == 4);
    Require(compact[24] == 0x1A && compact[25] == 0xE1);
});

Check("update picks the portable zip plus its checksum from release assets", () =>
{
    var assets = new[]
    {
        new ReleaseAsset("fast-torrent-download-0.2.0-win-x64-setup.exe", new Uri("https://github.com/o/r/releases/download/v0.2.0/fast-torrent-download-0.2.0-win-x64-setup.exe")),
        new ReleaseAsset("fast-torrent-download-0.2.0-win-x64-portable.zip", new Uri("https://github.com/o/r/releases/download/v0.2.0/fast-torrent-download-0.2.0-win-x64-portable.zip")),
        new ReleaseAsset("fast-torrent-download-0.2.0-win-x64-portable.zip.sha256", new Uri("https://github.com/o/r/releases/download/v0.2.0/fast-torrent-download-0.2.0-win-x64-portable.zip.sha256")),
    };
    var selected = ReleaseUpdateService.SelectPackage(assets);
    if (selected is not { } package)
    {
        throw new InvalidOperationException("Expected a package and checksum pair.");
    }
    Require(package.PackageUrl.AbsolutePath.EndsWith("-portable.zip"));
    Require(package.ChecksumUrl.AbsolutePath.EndsWith("-portable.zip.sha256"));
    Require(ReleaseUpdateService.SelectPackage(assets[..1]) is null);
});

Check("update rejects non-https and off-github download hosts", () =>
{
    Require(ReleaseUpdateService.IsDownloadHost(new Uri("https://github.com/o/r/download/x.zip")));
    Require(ReleaseUpdateService.IsDownloadHost(new Uri("https://objects.githubusercontent.com/abc123")));
    Require(!ReleaseUpdateService.IsDownloadHost(new Uri("http://github.com/o/r/download/x.zip")));
    Require(!ReleaseUpdateService.IsDownloadHost(new Uri("https://evil.example.com/x.zip")));
});

Check("update parses coreutils checksum files", () =>
{
    const string hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    Require(AppUpdateService.ParseChecksumFile($"{hash}  update-portable.zip\n", "update-portable.zip") == hash);
    Require(AppUpdateService.ParseChecksumFile($"{hash} *update-portable.zip\n", "update-portable.zip") == hash);
    Require(AppUpdateService.ParseChecksumFile($"{hash}  other.zip\n", "update-portable.zip") is null);
    Require(AppUpdateService.ParseChecksumFile("not-a-hash  update-portable.zip\n", "update-portable.zip") is null);
});

Check("engine tuning stays above library defaults for faster swarms and prefers encrypted handshakes", () =>
{
    Require(EnginePerformanceTuning.MaxConnections == 800);
    Require(EnginePerformanceTuning.MaxHalfOpenConnections == 150);
    Require(EnginePerformanceTuning.MaxConnectionsPerTorrent == 250);
    Require(EnginePerformanceTuning.DiskCacheBytes == 64 * 1024 * 1024);
    Require(EnginePerformanceTuning.UploadSlots == 16);
    Require(EnginePerformanceTuning.ConnectionTimeout == TimeSpan.FromSeconds(7));
    Require(EnginePerformanceTuning.WebSeedDelay == TimeSpan.FromSeconds(15));
    Require(EnginePerformanceTuning.MaxConnectionsPerTorrent <= EnginePerformanceTuning.MaxConnections);
    Require(EnginePerformanceTuning.MaxHalfOpenConnections <= EnginePerformanceTuning.MaxConnections);
    var preferred = EnginePerformanceTuning.GetAllowedEncryption(requireEncrypted: false);
    Require(preferred[0] == MonoTorrent.Connections.EncryptionType.RC4Header &&
            preferred[1] == MonoTorrent.Connections.EncryptionType.RC4Full &&
            preferred[2] == MonoTorrent.Connections.EncryptionType.PlainText);
    var required = EnginePerformanceTuning.GetAllowedEncryption(requireEncrypted: true);
    Require(!required.Contains(MonoTorrent.Connections.EncryptionType.PlainText));
});

Check("row detail shows seeder and peer counts", () =>
{
    var row = new TorrentRowViewModel(Snapshot(progress: 42, downloaded: 1_000, uploaded: 500, seeders: 5, leechers: 23));
    Require(row.DetailLabel.Contains("5 connected seeders") && row.DetailLabel.Contains("23 other peers"));
    row.Update(Snapshot(progress: 43, downloaded: 2_000, uploaded: 500, seeders: 6, leechers: 20));
    Require(row.DetailLabel.Contains("6 connected seeders") && row.DetailLabel.Contains("20 other peers"));
});

Check("show folder falls back to the default download folder when nothing is selected", () =>
{
    var settings = new AppSettings { DefaultDownloadFolder = Path.GetTempPath() };
    Require(MainViewModel.ResolveFolderToOpen(null, settings) == Path.GetTempPath());
    var missing = new AppSettings { DefaultDownloadFolder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")) };
    Require(MainViewModel.ResolveFolderToOpen(null, missing) is null);
    var selectedMissing = new TorrentRowViewModel(Snapshot(progress: 10, downloaded: 1, uploaded: 0));
    Require(MainViewModel.ResolveFolderToOpen(selectedMissing, settings) is null);
    var selected = new TorrentRowViewModel(new TorrentSnapshot("t", "t", Path.GetTempPath(), "Downloading", 10, 0, 0, 1, 0, 0, 0));
    Require(MainViewModel.ResolveFolderToOpen(selected, settings) == Path.GetTempPath());
});

Check("tracker prune drops proven-empty trackers but never strands the torrent", () =>
{
    var empty = new Uri("udp://empty.example.com:1337/announce");
    var full = new Uri("udp://full.example.com:1337/announce");
    var unknown = new Uri("udp://unknown.example.com:1337/announce");
    Require(TorrentEngineService.SelectTrackersToPrune([(empty, true, 0, 0), (full, true, 0, 5)]).SequenceEqual([empty]));
    Require(TorrentEngineService.SelectTrackersToPrune([(empty, true, 0, 0), (full, true, 0, 0)]).Count == 0);
    Require(TorrentEngineService.SelectTrackersToPrune([(unknown, false, 0, 0), (empty, true, 0, 0)]).SequenceEqual([empty]));
    Require(TorrentEngineService.SelectTrackersToPrune([(unknown, false, 0, 0)]).Count == 0);
    Require(TorrentEngineService.SelectTrackersToPrune([]).Count == 0);
});

Check("tracker rotation keeps a lone connected peer instead of restarting", () =>
{
    Require(TorrentEngineService.ShouldRotateTrackerCoverage(0, 0));
    Require(TorrentEngineService.ShouldRotateTrackerCoverage(1, 0));
    Require(!TorrentEngineService.ShouldRotateTrackerCoverage(1, 1));
    Require(!TorrentEngineService.ShouldRotateTrackerCoverage(2, 0));
    Require(!TorrentEngineService.ShouldRotateTrackerCoverage(0, 1));
});

Check("trackerless magnets fall back to known public trackers", () =>
{
    var trackers = TorrentEngineService.DefaultPublicTrackers;
    Require(trackers.Count >= 3);
    Require(trackers.All(uri => uri.IsAbsoluteUri && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == "udp")));
    Require(trackers.Select(uri => uri.AbsoluteUri).Distinct().Count() == trackers.Count);
});

Check("pause intents copy and normalize without aliasing", () =>
{
    var settings = new AppSettings();
    settings.PausedInfoHashes.Add("ABCDEF");
    settings.PausedInfoHashes.Add("  ");
    var clone = settings.Copy();
    Require(clone.PausedInfoHashes.Contains("abcdef"));
    clone.PausedInfoHashes.Add("123456");
    Require(!settings.PausedInfoHashes.Contains("123456"));
    settings.Normalize();
    Require(settings.PausedInfoHashes.Count == 1 && settings.PausedInfoHashes.Contains("ABCDEF"));
});

await CheckRestoredDestinationAsync();
passed++;
await CheckCompletedRemovalAsync(selectedOnly: true);
await CheckCompletedRemovalAsync(selectedOnly: false);
await CheckCompletedRemovalAsync(selectedOnly: true, manualRemoval: true);
passed += 3;
await LiveTransferCheck.RunAsync(complete: false);
await LiveTransferCheck.RunAsync(complete: true);
passed += 2;
Console.WriteLine($"PASS {passed} focused checks");
return 0;

void Check(string name, Action action)
{
    action();
    passed++;
    Console.WriteLine($"PASS {name}");
}

static TorrentSnapshot Snapshot(double progress, long downloaded, long uploaded, string state = "Seeding", int seeders = 0, int leechers = 0) => new(
    "test", "test", "test", state, progress, 0, 0, downloaded, uploaded, seeders, leechers);

static void Require(bool condition)
{
    if (!condition)
    {
        throw new InvalidOperationException("Assertion failed.");
    }
}

static async Task CheckCompletedRemovalAsync(bool selectedOnly, bool manualRemoval = false)
{
    var root = Path.Combine(Path.GetTempPath(), "fast-torrent-completion-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var data = new byte[16_384];
        Array.Fill(data, (byte)42);
        var hash = SHA1.HashData(data);
        var torrent = new BEncodedDictionary
        {
            ["info"] = new BEncodedDictionary
            {
                ["name"] = new BEncodedString("completion-check"),
                ["piece length"] = new BEncodedNumber(data.Length),
                ["pieces"] = new BEncodedString(hash.Concat(hash).ToArray()),
                ["files"] = new BEncodedList
                {
                    new BEncodedDictionary { ["length"] = new BEncodedNumber(data.Length), ["path"] = new BEncodedList { new BEncodedString("selected.bin") } },
                    new BEncodedDictionary { ["length"] = new BEncodedNumber(data.Length), ["path"] = new BEncodedList { new BEncodedString("skipped.bin") } }
                }
            }
        };
        var source = Path.Combine(root, "check.torrent");
        await File.WriteAllBytesAsync(source, torrent.Encode());
        using var engine = new ClientEngine(new EngineSettingsBuilder
        {
            CacheDirectory = Path.Combine(root, "cache"),
            DhtEndPoint = null,
            ListenEndPoints = new Dictionary<string, IPEndPoint>(),
            AllowLocalPeerDiscovery = false,
            AllowPortForwarding = false
        }.ToSettings());
        var paths = new AppPaths(Path.Combine(root, "app-state"));
        paths.EnsureDirectories();
        var service = new TorrentEngineService(paths);
        // Inject an isolated offline engine so the check never touches the user's saved queue.
        typeof(TorrentEngineService).GetField("_engine", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, engine);
        var preview = await service.PrepareAddAsync(source, root);
        await service.CommitAddAsync(preview, selectedOnly ? ["selected.bin"] : ["selected.bin", "skipped.bin"], startImmediately: false);
        ((BEncodedDictionary)torrent["info"])["name"] = new BEncodedString("survivor-check");
        var survivorSource = Path.Combine(root, "survivor.torrent");
        await File.WriteAllBytesAsync(survivorSource, torrent.Encode());
        var survivor = await service.PrepareAddAsync(survivorSource, root);
        await service.CommitAddAsync(survivor, ["selected.bin"], startImmediately: false);
        var savedEntries = (BEncodedList)BEncodedValue.Decode<BEncodedDictionary>(await File.ReadAllBytesAsync(paths.EngineStateFile))["Torrents"];
        Require(savedEntries.Count == 2);
        var added = savedEntries.Cast<BEncodedDictionary>().Single(item => ((BEncodedList)item["Files"]).Cast<BEncodedDictionary>().Any(file => ((BEncodedString)file["FullPath"]).Text.Contains("completion-check")));
        Require(((BEncodedString)added["SavePath"]).Text == managerSavePath());
        var addedFiles = ((BEncodedList)added["Files"]).Cast<BEncodedDictionary>().ToArray();
        Require(((BEncodedString)addedFiles.Single(file => ((BEncodedString)file["Path"]).Text == "selected.bin")["Priority"]).Text == "Normal");
        Require(((BEncodedString)addedFiles.Single(file => ((BEncodedString)file["Path"]).Text == "skipped.bin")["Priority"]).Text == (selectedOnly ? "DoNotDownload" : "Normal"));
        string managerSavePath() => engine.Torrents.Single(item => item.Name == "completion-check").SavePath;
        // An unconfirmed add must not enter the saved queue when another torrent is removed.
        ((BEncodedDictionary)torrent["info"])["name"] = new BEncodedString("pending-check");
        var pendingSource = Path.Combine(root, "pending.torrent");
        await File.WriteAllBytesAsync(pendingSource, torrent.Encode());
        var pending = await service.PrepareAddAsync(pendingSource, root);
        Require((await service.GetSnapshotsAsync()).Count == 2);
        var viewModel = new MainViewModel(service, new SettingsStore(paths), new ReleaseUpdateService());
        await viewModel.RefreshAsync();
        Require(viewModel.Torrents.Count == 2 && viewModel.QueueSummary == "2 torrents");
        viewModel.SelectedTorrent = viewModel.Torrents.Single(row => row.Name == "completion-check");
        var manager = engine.Torrents.Single(manager => manager.Name == "completion-check");
        var selected = manager.Files.Single(file => file.Path == "selected.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(selected.FullPath)!);
        await File.WriteAllBytesAsync(selected.FullPath, data);
        if (!selectedOnly)
        {
            await File.WriteAllBytesAsync(manager.Files.Single(file => file.Path == "skipped.bin").FullPath, data);
        }
        if (manualRemoval)
        {
            var backup = paths.EngineStateFile + ".backup";
            File.Move(paths.EngineStateFile, backup);
            Directory.CreateDirectory(paths.EngineStateFile);
            try
            {
                await viewModel.RemoveSelectedAsync();
                throw new InvalidOperationException("Expected a blocked state file to prevent removal.");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                viewModel.StatusMessage = exception.Message; // MainWindow.RunActionAsync shows this error in the app.
                Require(engine.Torrents.Contains(manager) && !viewModel.SelectedTorrent!.IsBusy);
                Require((await service.GetSnapshotsAsync()).Count == 2);
            }
            finally
            {
                Directory.Delete(paths.EngineStateFile);
                File.Move(backup, paths.EngineStateFile);
            }
            var showedStopping = false;
            viewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MainViewModel.StatusMessage) &&
                    viewModel.StatusMessage == "Stopping completion-check before removing…")
                {
                    Require(engine.Torrents.Contains(manager));
                    showedStopping = true;
                }
            };
            var refreshGate = (SemaphoreSlim)typeof(MainViewModel)
                .GetField("_refreshGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewModel)!;
            await refreshGate.WaitAsync();
            Task removal;
            try
            {
                removal = viewModel.RemoveSelectedAsync();
                using var removalTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (engine.Torrents.Contains(manager)) await Task.Delay(25, removalTimeout.Token);
                await Task.Delay(100);
                Require(!removal.IsCompleted); // Finishing removal must wait for an in-flight queue refresh.
            }
            finally { refreshGate.Release(); }
            await removal.WaitAsync(TimeSpan.FromSeconds(10));
            Require(showedStopping);
            Require(viewModel.StatusMessage == "Removed completion-check; downloaded files were kept.");
            await viewModel.RemoveSelectedAsync();
            Require(viewModel.StatusMessage == "Select a torrent first.");
        }
        else
        {
            await manager.HashCheckAsync(autoStart: false);
            Require(manager.PartialProgress == 100 && manager.Complete == !selectedOnly);
            var selection = (await service.GetSnapshotsAsync()).Single(item => item.Id == preview.Id);
            Require(selection.SelectedBytes == data.Length * (selectedOnly ? 1 : 2));
            Require(selection.VerifiedBytes == selection.SelectedBytes && selection.Progress == 100);
            await service.StartAsync(preview.Id);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (manager.State is not (TorrentState.Downloading or TorrentState.Seeding))
            {
                if (manager.State == TorrentState.Error) throw new InvalidOperationException($"Test engine error: {manager.Error?.Reason}: {manager.Error?.Exception}");
                if (timeout.IsCancellationRequested) throw new InvalidOperationException($"Start did not settle: state={manager.State}, hashChecked={manager.HashChecked}, progress={manager.PartialProgress}");
                await Task.Delay(50);
            }
        }
        await viewModel.RefreshAsync();
        using (var removalTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            while (viewModel.Torrents.Count != 1)
            {
                await Task.Delay(50, removalTimeout.Token);
                await viewModel.RefreshAsync();
            }
        }
        Require(viewModel.Torrents.Count == 1 && viewModel.SelectedTorrent is null && viewModel.QueueSummary == "1 torrent");
        Require((await service.GetSnapshotsAsync()).Single().Id == survivor.Id);
        Require(engine.Torrents.Count == 2 && service.PausedInfoHashes.Count == 1);
        Require((await File.ReadAllBytesAsync(selected.FullPath)).SequenceEqual(data));
        Require(File.Exists(source));
        // Read the application's persisted state without saving or shutting down the service.
        var persisted = BEncodedValue.Decode<BEncodedDictionary>(await File.ReadAllBytesAsync(paths.EngineStateFile));
        Require(((BEncodedList)persisted["Torrents"]).Count == 1);
        using var restored = await ClientEngine.RestoreStateAsync(paths.EngineStateFile);
        Require(restored.Torrents.Single().Name == "survivor-check");
        if (!manualRemoval)
        {
            Require(viewModel.Notification?.Message.StartsWith("Completed completion-check") == true);
            Require(viewModel.Notification?.Destination == root);
            Require(viewModel.Activity.Any(item => item.CanOpenFolder && item.Message.StartsWith("Completed completion-check")));
            viewModel.ExpireNotification(DateTimeOffset.UtcNow.AddMinutes(1));
            Require(!viewModel.HasNotification && viewModel.StatusMessage == "Ready.");
        }
        for (var i = 0; i < 55; i++) viewModel.StatusMessage = $"Activity check {i}";
        Require(viewModel.Activity.Count == 50 && viewModel.Activity[0].Message == "Activity check 54");
        await service.CancelAddAsync(pending.Id);
        Console.WriteLine($"PASS offline {(manualRemoval ? "manual removal" : selectedOnly ? "selected-file completion" : "full completion")} persists immediately, keeps files and excludes pending adds after restore");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static async Task CheckRestoredDestinationAsync()
{
    var root = Path.Combine(Path.GetTempPath(), "fast-torrent-restore-" + Guid.NewGuid().ToString("N"));
    var paths = new AppPaths(root);
    paths.EnsureDirectories();
    try
    {
        var source = Path.Combine(root, "restore.torrent");
        var torrent = new BEncodedDictionary
        {
            ["info"] = new BEncodedDictionary
            {
                ["name"] = new BEncodedString("restore-check"), ["length"] = new BEncodedNumber(1),
                ["piece length"] = new BEncodedNumber(16_384), ["pieces"] = new BEncodedString(SHA1.HashData(new byte[1]))
            }
        };
        await File.WriteAllBytesAsync(source, torrent.Encode());
        using (var engine = new ClientEngine(new EngineSettingsBuilder
        {
            CacheDirectory = paths.CacheDirectory, DhtEndPoint = null, AllowPortForwarding = false,
            AllowLocalPeerDiscovery = false, ListenEndPoints = new Dictionary<string, IPEndPoint>()
        }.ToSettings()))
        {
            var service = new TorrentEngineService(paths);
            typeof(TorrentEngineService).GetField("_engine", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, engine);
            var preview = await service.PrepareAddAsync(source, root);
            Directory.CreateDirectory(paths.EngineStateFile);
            try
            {
                await service.CommitAddAsync(preview, ["restore-check"], startImmediately: false);
                throw new InvalidOperationException("Expected a blocked state path to reject the add.");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Require((await service.GetSnapshotsAsync()).Count == 0);
            }
            finally { Directory.Delete(paths.EngineStateFile); }
            await service.CommitAddAsync(preview, ["restore-check"], startImmediately: false);
        }
        using var port = new TcpListener(IPAddress.Loopback, 0);
        port.Start();
        var listenPort = ((IPEndPoint)port.LocalEndpoint).Port;
        port.Stop();
        await using var reopened = new TorrentEngineService(paths);
        await reopened.InitializeAsync(new AppSettings { ListenPort = listenPort, EnableDht = false, EnablePeerExchange = false, EnableLocalPeerDiscovery = false, EnablePortForwarding = false });
        var reloaded = (await reopened.GetSnapshotsAsync()).Single();
        Require(reloaded.Destination == root && reloaded.State == "Stopped");
        Require(MainViewModel.ResolveFolderToOpen(new TorrentRowViewModel(reloaded), new AppSettings()) == root);
        Console.WriteLine("PASS an added torrent survives restart without shutdown, preserving pause intent and destination");
    }
    finally { Directory.Delete(root, recursive: true); }
}
