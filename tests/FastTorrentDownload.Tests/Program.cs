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

if (Array.IndexOf(args, "--ui-shots") is var shotsIndex and >= 0)
{
    UiSmoke.ShotsDirectory = Path.GetFullPath(args.ElementAtOrDefault(shotsIndex + 1) ?? "ui-shots");
    return UiSmoke.Run();
}
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
    Require((data with { ConnectedPeers = 8, ReceivingPeers = 2, AvailablePiecesPercent = 100, IncomingPeers = 0, ListenPort = 51413 }).IsInboundPortLikelyClosed);
    Require((data with { ConnectedPeers = 8, ReceivingPeers = 2, AvailablePiecesPercent = 100, IncomingPeers = 0, ListenPort = 51413 }).Diagnosis(500).Contains("inbound port 51413 appears closed"));
    Require(!(data with { ConnectedPeers = 8, ReceivingPeers = 2, AvailablePiecesPercent = 100, IncomingPeers = 3, ListenPort = 51413 }).IsInboundPortLikelyClosed);
    Require((data with { ConnectedPeers = 8, ReceivingPeers = 2, AvailablePiecesPercent = 100, IncomingPeers = 3, ListenPort = 51413 }).Describe(500).Contains("incoming on port 51413: 3"));
    var netInfo = NetworkBinding.DetectLocalNetworkInfo(51413);
    Require(netInfo.ListenPort == 51413 && !string.IsNullOrWhiteSpace(netInfo.HostName) && !string.IsNullOrWhiteSpace(netInfo.LocalIpv4));
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
    var samplePiece = new byte[16_384];
    var sampleDict = new BEncodedDictionary
    {
        ["announce"] = new BEncodedString("http://cache-host.example/announce"),
        ["info"] = new BEncodedDictionary
        {
            ["name"] = new BEncodedString("fast-magnet-cache.bin"),
            ["length"] = new BEncodedNumber(samplePiece.Length),
            ["piece length"] = new BEncodedNumber(samplePiece.Length),
            ["pieces"] = new BEncodedString(SHA1.HashData(samplePiece))
        }
    };
    var sampleBytes = sampleDict.Encode();
    var sampleTorrent = Torrent.Load(sampleBytes);
    var matchingMagnet = new MagnetLink(sampleTorrent.InfoHashes);
    var wrongMagnet = MagnetLink.Parse("magnet:?xt=urn:btih:792b9fa5793e46c51db29f59c6a9037cf20492f4");
    Require(!TorrentEngineService.TryCacheMagnetTorrentBytes(wrongMagnet, sampleBytes, out _));
    Require(TorrentEngineService.TryCacheMagnetTorrentBytes(matchingMagnet, sampleBytes, out var verified) && verified is not null);
    // Trackers supplied by a cache host are not covered by the info-hash and must be dropped.
    Require(verified!.AnnounceUrls.Count == 0 && verified.InfoHashes == sampleTorrent.InfoHashes);
    Require(TorrentEngineService.TryGetVerifiedCachedTorrent(matchingMagnet, out var fromCache) && fromCache?.Name == "fast-magnet-cache.bin");
    Require(FastTorrentDownload.Views.MainWindow.PendingTorrentName("magnet:?xt=urn:btih:24eb124f02db18c31e3e1fd33d8056e5cb0f054d&dn=Torrentio%0A1080p") == "Torrentio 1080p");
    Require(FastTorrentDownload.Views.MainWindow.PendingTorrentName("magnet:?xt=urn:btih:24eb124f02db18c31e3e1fd33d8056e5cb0f054d") == "Magnet link");
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

Check("dht bootstrap nodes use flat 26-byte compact layout and unwrap bencoded saved lists", () =>
{
    var nodeId = Enumerable.Range(1, 20).Select(i => (byte)i).ToArray();
    var compact = DhtBootstrapCache.ToCompactNode(IPAddress.Parse("1.2.3.4"), 6881, nodeId);
    Require(compact.Length == 26);
    Require(compact.Take(20).SequenceEqual(nodeId));
    Require(compact[20] == 1 && compact[21] == 2 && compact[22] == 3 && compact[23] == 4);
    Require(compact[24] == 0x1A && compact[25] == 0xE1);
    var savedList = new BEncodedList { new BEncodedString(compact) }.Encode();
    var extractedFromList = DhtBootstrapCache.ExtractSavedCompactNodes(savedList);
    Require(extractedFromList.Count == 1 && extractedFromList[0].SequenceEqual(compact));
    var extractedFromFlat = DhtBootstrapCache.ExtractSavedCompactNodes(compact);
    Require(extractedFromFlat.Count == 1 && extractedFromFlat[0].SequenceEqual(compact));
    Require(DhtBootstrapCache.ExtractSavedCompactNodes(new BEncodedList().Encode()).Count == 0);
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

Check("active-download limit queues extra torrents and treats transfer states as active", () =>
{
    Require(!TorrentEngineService.ShouldQueue(0, 50));
    Require(!TorrentEngineService.ShouldQueue(2, 1));
    Require(TorrentEngineService.ShouldQueue(2, 2));
    Require(TorrentEngineService.IsActiveState(TorrentState.Downloading) && TorrentEngineService.IsActiveState(TorrentState.Hashing) && TorrentEngineService.IsActiveState(TorrentState.Metadata));
    Require(!TorrentEngineService.IsActiveState(TorrentState.Stopped) && !TorrentEngineService.IsActiveState(TorrentState.Error) && !TorrentEngineService.IsActiveState(TorrentState.Paused));
    var settings = new AppSettings { MaximumActiveDownloads = -3 };
    settings.Normalize();
    Require(settings.MaximumActiveDownloads == 0 && !settings.UsePublicMetadataCaches && settings.CloseToTray && !settings.StartWithWindows);
    var queued = new TorrentRowViewModel(Snapshot(10, 0, 0, state: "Queued"));
    Require(queued.CanPause && !queued.CanResume && queued.CanAct);
    var stopped = new TorrentRowViewModel(Snapshot(10, 0, 0, state: "Stopped"));
    Require(stopped.CanResume && !stopped.CanPause);
    var downloading = new TorrentRowViewModel(Snapshot(10, 0, 0, state: "Downloading"));
    Require(downloading.CanPause && !downloading.CanResume);
    downloading.Operation = "Moving…";
    Require(!downloading.CanPause && !downloading.CanResume && downloading.IsBusy);
});

Check("a download with no payload is reported as stalled with the reason and backs off reannounces", () =>
{
    Require(TorrentEngineService.IsStalled(TorrentState.Downloading, TorrentEngineService.StallThreshold));
    Require(!TorrentEngineService.IsStalled(TorrentState.Downloading, TorrentEngineService.StallThreshold - TimeSpan.FromSeconds(1)));
    Require(!TorrentEngineService.IsStalled(TorrentState.Seeding, TimeSpan.FromHours(1)));
    Require(!TorrentEngineService.IsStalled(TorrentState.Stopped, TimeSpan.FromHours(1)));
    Require(!TorrentEngineService.ShouldReannounceStarvedTorrent(TorrentState.Downloading, 2, TimeSpan.FromMinutes(3), stalled: true));
    Require(TorrentEngineService.ShouldReannounceStarvedTorrent(TorrentState.Downloading, 2, TimeSpan.FromMinutes(15), stalled: true));
    var flowing = new TorrentDiagnostics(20, 2, 0, 0, 62.7, "Ready", 50, 0, 0, []);
    Require(!flowing.IsStalled && flowing.StallSummary is null);
    var dead = flowing with { SwarmSeeders = 0, SwarmLeechers = 17, StalledFor = TimeSpan.FromMinutes(6) };
    Require(dead.IsStalled && dead.HasNoSeeders);
    Require(dead.StallSummary == $"No seeders in the swarm · 17 other downloaders waiting · connected peers hold {62.7:0.0}%");
    Require(dead.Diagnosis(0).StartsWith("Stalled: No seeders in the swarm") && dead.Diagnosis(0).Contains("another source"));
    Require(dead.Describe(0).Contains("Swarm reported by trackers: 0 seeders · 17 downloaders"));
    Require(flowing.Describe(0).Contains("Swarm reported by trackers: not available yet"));
    // A tracker-reported seeder that is not connected is a reachability problem, not a dead swarm.
    var unreachable = dead with { SwarmSeeders = 3 };
    Require(!unreachable.HasNoSeeders && unreachable.StallSummary == $"Connected peers hold only {62.7:0.0}% of the data");
    Require((dead with { ConnectedPeers = 0, KnownPeers = 0, SwarmSeeders = null }).StallSummary == "No peers found");
    var row = new TorrentRowViewModel(Snapshot(62.7, 0, 0, state: "Stalled") with { Diagnostics = dead, SelectedBytes = 2048, VerifiedBytes = 1024 });
    Require(row.CanPause && !row.CanResume && row.StateLabel == "Stalled");
    Require(row.DetailLabel.Contains("No seeders in the swarm") && !row.DetailLabel.Contains("ETA"));
});

Check("second-instance sources are filtered to magnets and torrent files", () =>
{
    var sources = SingleInstanceChannel.ExtractSources(["--minimized", " magnet:?xt=urn:btih:abc ", @"C:\x\file.torrent", "notes.txt", ""]);
    Require(sources.SequenceEqual(["magnet:?xt=urn:btih:abc", @"C:\x\file.torrent"]));
    Require(!SingleInstanceChannel.TryForward([]));
});

Check("starved downloads reannounce periodically", () =>
{
    Require(TorrentEngineService.ShouldReannounceStarvedTorrent(TorrentState.Downloading, 20, TimeSpan.FromMinutes(3)));
    Require(!TorrentEngineService.ShouldReannounceStarvedTorrent(TorrentState.Downloading, 60, TimeSpan.FromMinutes(3)));
    Require(!TorrentEngineService.ShouldReannounceStarvedTorrent(TorrentState.Downloading, 20, TimeSpan.FromMinutes(1)));
    Require(!TorrentEngineService.ShouldReannounceStarvedTorrent(TorrentState.Seeding, 5, TimeSpan.FromMinutes(5)));
});

Check("trackerless magnets fall back to known public trackers", () =>
{
    var trackers = TorrentEngineService.DefaultPublicTrackers;
    Require(trackers.Count >= 3);
    Require(trackers.All(uri => uri.IsAbsoluteUri && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == "udp")));
    Require(trackers.Select(uri => uri.AbsoluteUri).Distinct().Count() == trackers.Count);
});

Check("torrent file paths must stay inside the download folder", () =>
{
    var dest = Path.Combine(Path.GetTempPath(), "ftd-safe-dest", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dest);
    try
    {
        Require(PathGuard.IsSafeTorrentFilePath(dest, "video.mkv"));
        Require(PathGuard.IsSafeTorrentFilePath(dest, Path.Combine("season 1", "episode.mkv")));
        Require(!PathGuard.IsSafeTorrentFilePath(dest, Path.Combine("..", "outside.bin")));
        Require(!PathGuard.IsSafeTorrentFilePath(dest, "/etc/passwd"));
        Require(!PathGuard.IsSafeTorrentFilePath(dest, @"\Users\Public\pwned.txt"));
        Require(!PathGuard.IsSafeTorrentFilePath(dest, @"C:\Windows\win.ini"));
        Require(!PathGuard.IsSafeTorrentFilePath(dest, @"foo\..\..\secret.txt"));
        Require(!PathGuard.IsSafeTorrentFilePath(dest, "foo/../../secret.txt"));
        Require(!PathGuard.IsSafeTorrentFilePath(dest, "foo/.. /secret.txt"));
        Require(!PathGuard.IsSafeTorrentFilePath(dest, "video.mkv:stream"));
        Require(!PathGuard.IsSafeTorrentFilePath(dest, ""));
        Require(!PathGuard.IsSafeTorrentFilePath(dest, "   "));
        try
        {
            PathGuard.ThrowIfUnsafeTorrentFiles(dest, [@"\Users\Public\pwned.txt"]);
            throw new InvalidOperationException("Expected an unsafe torrent path to be rejected.");
        }
        catch (InvalidDataException)
        {
            // Expected: a rooted torrent path must never reach the engine write path.
        }
    }
    finally
    {
        try { Directory.Delete(dest, recursive: true); } catch { }
    }
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
await CheckSingleInstanceForwardingAsync();
await CheckFileSelectionDuplicateAndQueueAsync();
await CheckStalledTorrentFreesQueueSlotAsync();
await CheckDeadSwarmIsNamedFromTrackerScrapeAsync();
passed += 5;
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

static async Task CheckSingleInstanceForwardingAsync()
{
    // A unique pipe name keeps the check away from an installed instance's real pipe.
    var pipeName = "WASD.FastTorrentDownload.test-" + Guid.NewGuid().ToString("N");
    using var channel = new SingleInstanceChannel(pipeName);
    var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    channel.Start(source => received.TrySetResult(source));
    await Task.Delay(200); // Let the pipe server come up before the client connects.
    Require(SingleInstanceChannel.TryForward(["magnet:?xt=urn:btih:792b9fa5793e46c51db29f59c6a9037cf20492f4&dn=Forwarded"], pipeName));
    var source = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Require(source.StartsWith("magnet:?xt=urn:btih:792b9fa5", StringComparison.Ordinal));
    Console.WriteLine("PASS a second launch forwards its magnet link to the running instance");
}

static async Task CheckFileSelectionDuplicateAndQueueAsync()
{
    var root = Path.Combine(Path.GetTempPath(), "fast-torrent-queue-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var data = new byte[16_384];
        Array.Fill(data, (byte)9);
        var hash = SHA1.HashData(data);
        BEncodedDictionary Torrent(string name) => new()
        {
            ["info"] = new BEncodedDictionary
            {
                ["name"] = new BEncodedString(name),
                ["piece length"] = new BEncodedNumber(data.Length),
                ["pieces"] = new BEncodedString(hash.Concat(hash).ToArray()),
                ["files"] = new BEncodedList
                {
                    new BEncodedDictionary { ["length"] = new BEncodedNumber(data.Length), ["path"] = new BEncodedList { new BEncodedString("keep.bin") } },
                    new BEncodedDictionary { ["length"] = new BEncodedNumber(data.Length), ["path"] = new BEncodedList { new BEncodedString("drop.bin") } }
                }
            }
        };
        var first = Path.Combine(root, "first.torrent");
        var second = Path.Combine(root, "second.torrent");
        await File.WriteAllBytesAsync(first, Torrent("queue-first").Encode());
        await File.WriteAllBytesAsync(second, Torrent("queue-second").Encode());
        using var engine = new ClientEngine(new EngineSettingsBuilder
        {
            CacheDirectory = Path.Combine(root, "cache"), DhtEndPoint = null, AllowPortForwarding = false,
            AllowLocalPeerDiscovery = false, ListenEndPoints = new Dictionary<string, IPEndPoint>()
        }.ToSettings());
        var paths = new AppPaths(Path.Combine(root, "app-state"));
        paths.EnsureDirectories();
        var service = new TorrentEngineService(paths);
        typeof(TorrentEngineService).GetField("_engine", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, engine);
        typeof(TorrentEngineService).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, new AppSettings { DefaultDownloadFolder = root, MaximumActiveDownloads = 1 });

        // The folder appears only once the add is confirmed.
        var destination = Path.Combine(root, "payload");
        var previewFirst = await service.PrepareAddAsync(first, destination);
        Require(!Directory.Exists(destination));
        try
        {
            await service.PrepareAddAsync(first, destination);
            throw new InvalidOperationException("Expected a duplicate add to be rejected.");
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("already in the queue"))
        {
        }
        await service.CommitAddAsync(previewFirst, ["keep.bin", "drop.bin"], startImmediately: false);
        Require(Directory.Exists(destination));

        // Reselecting files after the add changes priorities and persists them.
        var files = await service.GetFilesAsync(previewFirst.Id);
        Require(files.Files.Count == 2 && files.Files.All(file => file.Selected));
        await service.SetSelectedFilesAsync(previewFirst.Id, ["keep.bin"]);
        files = await service.GetFilesAsync(previewFirst.Id);
        Require(files.Files.Single(file => file.Path == "keep.bin").Selected && !files.Files.Single(file => file.Path == "drop.bin").Selected);
        var saved = (BEncodedList)BEncodedValue.Decode<BEncodedDictionary>(await File.ReadAllBytesAsync(paths.EngineStateFile))["Torrents"];
        var savedFiles = ((BEncodedList)((BEncodedDictionary)saved.Single())["Files"]).Cast<BEncodedDictionary>().ToArray();
        Require(((BEncodedString)savedFiles.Single(file => ((BEncodedString)file["Path"]).Text == "drop.bin")["Priority"]).Text == "DoNotDownload");

        // With one active slot, the second torrent waits as Queued and starts once the first stops.
        var previewSecond = await service.PrepareAddAsync(second, destination);
        await service.CommitAddAsync(previewSecond, ["keep.bin"], startImmediately: false);
        await service.StartAsync(previewFirst.Id);
        await service.StartAsync(previewSecond.Id);
        var snapshots = await service.GetSnapshotsAsync();
        Require(snapshots.Single(item => item.Id == previewFirst.Id).State != "Queued");
        Require(snapshots.Single(item => item.Id == previewSecond.Id).State == "Queued");
        await service.PauseAsync(previewFirst.Id);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while ((await service.GetSnapshotsAsync()).Single(item => item.Id == previewSecond.Id).State is "Queued" or "Stopped")
        {
            await Task.Delay(50, timeout.Token);
        }
        Require((await service.GetSnapshotsAsync()).Single(item => item.Id == previewFirst.Id).State == "Stopped");

        // Moving relocates the payload root and keeps the row's destination in sync.
        var moved = Path.Combine(root, "moved");
        await service.MoveAsync(previewFirst.Id, moved);
        var movedSnapshot = (await service.GetSnapshotsAsync()).Single(item => item.Id == previewFirst.Id);
        Require(movedSnapshot.Destination == moved && engine.Torrents.Single(item => item.Name == "queue-first").SavePath == moved);
        await service.PauseAsync(previewSecond.Id);
        await service.RemoveAsync(previewFirst.Id);
        await service.RemoveAsync(previewSecond.Id);
        Console.WriteLine("PASS offline duplicate rejection, deferred folder creation, file reselection persistence, active-download queue and move");
    }
    finally
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }
}

static async Task CheckStalledTorrentFreesQueueSlotAsync()
{
    var root = Path.Combine(Path.GetTempPath(), "fast-torrent-stalled-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var threshold = TorrentEngineService.StallThreshold;
    try
    {
        var data = new byte[16_384];
        BEncodedDictionary Torrent(string name) => new()
        {
            ["info"] = new BEncodedDictionary
            {
                ["name"] = new BEncodedString(name), ["length"] = new BEncodedNumber(data.Length),
                ["piece length"] = new BEncodedNumber(data.Length), ["pieces"] = new BEncodedString(SHA1.HashData(System.Text.Encoding.ASCII.GetBytes(name)))
            }
        };
        using var engine = new ClientEngine(new EngineSettingsBuilder
        {
            CacheDirectory = Path.Combine(root, "cache"), DhtEndPoint = null, AllowPortForwarding = false,
            AllowLocalPeerDiscovery = false, ListenEndPoints = new Dictionary<string, IPEndPoint>()
        }.ToSettings());
        var paths = new AppPaths(Path.Combine(root, "app-state"));
        paths.EnsureDirectories();
        var service = new TorrentEngineService(paths);
        typeof(TorrentEngineService).GetField("_engine", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, engine);
        typeof(TorrentEngineService).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, new AppSettings { DefaultDownloadFolder = root, MaximumActiveDownloads = 1 });
        var ids = new List<string>();
        foreach (var name in new[] { "dead-swarm", "waiting-behind" })
        {
            var source = Path.Combine(root, name + ".torrent");
            await File.WriteAllBytesAsync(source, Torrent(name).Encode());
            var preview = await service.PrepareAddAsync(source, root);
            await service.CommitAddAsync(preview, [name], startImmediately: false);
            ids.Add(preview.Id);
        }

        var viewModel = new MainViewModel(service, new SettingsStore(paths), new ReleaseUpdateService());
        await service.StartAsync(ids[0]);
        await service.StartAsync(ids[1]);
        await viewModel.RefreshAsync();
        Require(viewModel.Torrents.Single(row => row.Id == ids[1]).State == "Queued");

        // Nothing can ever arrive (no peers, no trackers): once the threshold passes the row must say so.
        TorrentEngineService.StallThreshold = TimeSpan.FromMilliseconds(300);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (viewModel.Torrents.Single(row => row.Id == ids[0]).State != "Stalled")
        {
            await Task.Delay(100, timeout.Token);
            await viewModel.RefreshAsync();
        }
        var stalled = viewModel.Torrents.Single(row => row.Id == ids[0]);
        Require(stalled.DetailLabel.Contains("No peers found") && stalled.DiagnosticsText.Contains("Stalled for"));
        Require(viewModel.Activity.Any(item => item.Message.StartsWith("dead-swarm is stalled: No peers found")));

        // The stalled torrent no longer holds the only active slot, so the queued one starts.
        while (viewModel.Torrents.Single(row => row.Id == ids[1]).State is "Queued" or "Stopped")
        {
            await Task.Delay(100, timeout.Token);
            await viewModel.RefreshAsync();
        }

        // Pausing clears the stall; with the real threshold back, a resume starts a fresh episode.
        TorrentEngineService.StallThreshold = threshold;
        await service.PauseAsync(ids[0]);
        await viewModel.RefreshAsync();
        Require(viewModel.Torrents.Single(row => row.Id == ids[0]).State == "Stopped");
        await service.PauseAsync(ids[1]);
        await service.RemoveAsync(ids[0]);
        await service.RemoveAsync(ids[1]);
        Console.WriteLine("PASS offline stalled torrent reports its reason, notifies once and frees its active-download slot");
    }
    finally
    {
        TorrentEngineService.StallThreshold = threshold;
        try { Directory.Delete(root, recursive: true); } catch { }
    }
}

static async Task CheckDeadSwarmIsNamedFromTrackerScrapeAsync()
{
    var root = Path.Combine(Path.GetTempPath(), "fast-torrent-scrape-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var threshold = TorrentEngineService.StallThreshold;
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    using var tracker = new TcpListener(IPAddress.Loopback, 0);
    tracker.Start();
    var announce = $"http://127.0.0.1:{((IPEndPoint)tracker.LocalEndpoint).Port}/announce";
    byte[] infoHash = [];
    var scrapes = 0;
    var server = ServeAsync();
    try
    {
        var encoded = new BEncodedDictionary
        {
            ["announce"] = new BEncodedString(announce),
            ["info"] = new BEncodedDictionary
            {
                ["name"] = new BEncodedString("no-seeders"), ["length"] = new BEncodedNumber(16_384),
                ["piece length"] = new BEncodedNumber(16_384), ["pieces"] = new BEncodedString(SHA1.HashData(new byte[] { 1, 2, 3 }))
            }
        };
        var source = Path.Combine(root, "no-seeders.torrent");
        await File.WriteAllBytesAsync(source, encoded.Encode());
        infoHash = Torrent.Load(source).InfoHashes.V1!.Span.ToArray();
        using var engine = new ClientEngine(new EngineSettingsBuilder
        {
            CacheDirectory = Path.Combine(root, "cache"), DhtEndPoint = null, AllowPortForwarding = false,
            AllowLocalPeerDiscovery = false, ListenEndPoints = new Dictionary<string, IPEndPoint>()
        }.ToSettings());
        var paths = new AppPaths(Path.Combine(root, "app-state"));
        paths.EnsureDirectories();
        var service = new TorrentEngineService(paths);
        typeof(TorrentEngineService).GetField("_engine", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, engine);
        var preview = await service.PrepareAddAsync(source, root);
        await service.CommitAddAsync(preview, ["no-seeders"], startImmediately: true);
        TorrentEngineService.StallThreshold = TimeSpan.FromMilliseconds(300);
        TorrentSnapshot snapshot;
        do
        {
            await Task.Delay(100, stop.Token);
            snapshot = (await service.GetSnapshotsAsync()).Single();
        }
        while (snapshot.State != "Stalled" || snapshot.Diagnostics?.SwarmSeeders is null);
        Require(scrapes > 0);
        Require(snapshot.Diagnostics!.SwarmSeeders == 0 && snapshot.Diagnostics.SwarmLeechers == 17 && snapshot.Diagnostics.HasNoSeeders);
        Require(snapshot.Diagnostics.StallSummary!.StartsWith("No seeders in the swarm · 17 other downloaders waiting"));
        Require(new TorrentRowViewModel(snapshot).DetailLabel.Contains("No seeders in the swarm"));
        TorrentEngineService.StallThreshold = threshold;
        await service.RemoveAsync(preview.Id);
        Console.WriteLine("PASS loopback tracker scrape names a dead swarm: 0 seeders, 17 downloaders waiting");
    }
    finally
    {
        TorrentEngineService.StallThreshold = threshold;
        stop.Cancel();
        tracker.Stop();
        await server;
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    async Task ServeAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                using var client = await tracker.AcceptTcpClientAsync(stop.Token);
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);
                var request = await reader.ReadLineAsync(stop.Token) ?? string.Empty;
                while (await reader.ReadLineAsync(stop.Token) is { Length: > 0 }) { }
                byte[] body;
                if (request.Contains("/scrape", StringComparison.Ordinal))
                {
                    scrapes++;
                    body = new BEncodedDictionary
                    {
                        ["files"] = new BEncodedDictionary
                        {
                            [new BEncodedString(infoHash)] = new BEncodedDictionary
                            {
                                ["complete"] = new BEncodedNumber(0), ["downloaded"] = new BEncodedNumber(0), ["incomplete"] = new BEncodedNumber(17)
                            }
                        }
                    }.Encode();
                }
                else
                {
                    body = new BEncodedDictionary { ["interval"] = new BEncodedNumber(1800), ["peers"] = new BEncodedString(Array.Empty<byte>()) }.Encode();
                }

                var headers = System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(headers, stop.Token);
                await stream.WriteAsync(body, stop.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) when (stop.IsCancellationRequested) { }
        catch (IOException) when (stop.IsCancellationRequested) { }
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
