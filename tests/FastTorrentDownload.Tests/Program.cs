using FastTorrentDownload.Models;
using FastTorrentDownload.Services;
using FastTorrentDownload.ViewModels;
using System.Linq;
using System.Net;

var passed = 0;

Check("ratio policy pauses completed torrents at the target", () =>
{
    var settings = new AppSettings { SeedRatioTarget = 1.0 };
    var completed = Snapshot(progress: 100, downloaded: 1_000, uploaded: 1_000);
    Require(QueueCoordinator.ShouldPauseForRatio(completed, settings));
});

Check("ratio policy keeps incomplete or below-target torrents active", () =>
{
    var settings = new AppSettings { SeedRatioTarget = 1.0 };
    Require(!QueueCoordinator.ShouldPauseForRatio(Snapshot(progress: 99.9, downloaded: 1_000, uploaded: 2_000), settings));
    Require(!QueueCoordinator.ShouldPauseForRatio(Snapshot(progress: 100, downloaded: 1_000, uploaded: 999), settings));
});

Check("zero ratio target leaves seeding under user control", () =>
{
    var settings = new AppSettings { SeedRatioTarget = 0 };
    Require(!QueueCoordinator.ShouldPauseForRatio(Snapshot(progress: 100, downloaded: 1_000, uploaded: 10_000), settings));
});

Check("a stopped completed torrent is not stopped again by ratio enforcement", () =>
{
    var settings = new AppSettings { SeedRatioTarget = 1.0 };
    Require(!QueueCoordinator.ShouldPauseForRatio(Snapshot(progress: 100, downloaded: 1_000, uploaded: 2_000, state: "Stopped"), settings));
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

Check("engine tuning stays above library defaults for faster swarms", () =>
{
    Require(EnginePerformanceTuning.MaxConnections == 500);
    Require(EnginePerformanceTuning.MaxHalfOpenConnections == 50);
    Require(EnginePerformanceTuning.MaxConnectionsPerTorrent == 100);
    Require(EnginePerformanceTuning.DiskCacheBytes == 32 * 1024 * 1024);
    Require(EnginePerformanceTuning.WebSeedDelay == TimeSpan.FromSeconds(15));
    Require(EnginePerformanceTuning.MaxConnectionsPerTorrent <= EnginePerformanceTuning.MaxConnections);
    Require(EnginePerformanceTuning.MaxHalfOpenConnections <= EnginePerformanceTuning.MaxConnections);
});

Check("row detail shows seeder and peer counts", () =>
{
    var row = new TorrentRowViewModel(Snapshot(progress: 42, downloaded: 1_000, uploaded: 500, seeders: 5, leechers: 23));
    Require(row.DetailLabel.Contains("5 seeders") && row.DetailLabel.Contains("23 peers"));
    row.Update(Snapshot(progress: 43, downloaded: 2_000, uploaded: 500, seeders: 6, leechers: 20));
    Require(row.DetailLabel.Contains("6 seeders") && row.DetailLabel.Contains("20 peers"));
});

Check("show folder falls back to the default download folder when nothing is selected", () =>
{
    var settings = new AppSettings { DefaultDownloadFolder = Path.GetTempPath() };
    Require(MainViewModel.ResolveFolderToOpen(null, settings) == Path.GetTempPath());
    var missing = new AppSettings { DefaultDownloadFolder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")) };
    Require(MainViewModel.ResolveFolderToOpen(null, missing) is null);
    var selectedMissing = new TorrentRowViewModel(Snapshot(progress: 10, downloaded: 1, uploaded: 0));
    Require(MainViewModel.ResolveFolderToOpen(selectedMissing, settings) is null);
    var selected = new TorrentRowViewModel(new TorrentSnapshot("t", "t", Path.GetTempPath(), "Downloading", 10, 0, 0, 1, 0, false, 0, 0));
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

Check("ratio-enforced pauses are persisted when they diverge from settings", () =>
{
    var persisted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "abc" };
    Require(MainViewModel.PauseIntentsDiffer(persisted, live));
    Require(MainViewModel.PauseIntentsDiffer(live, persisted));
    Require(!MainViewModel.PauseIntentsDiffer(live, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ABC" }));
    Require(!MainViewModel.PauseIntentsDiffer(persisted, new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
});

Console.WriteLine($"PASS {passed} focused checks");
return 0;

void Check(string name, Action action)
{
    action();
    passed++;
    Console.WriteLine($"PASS {name}");
}

static TorrentSnapshot Snapshot(double progress, long downloaded, long uploaded, string state = "Seeding", int seeders = 0, int leechers = 0) => new(
    "test", "test", "test", state, progress, 0, 0, downloaded, uploaded, progress >= 100, seeders, leechers);

static void Require(bool condition)
{
    if (!condition)
    {
        throw new InvalidOperationException("Assertion failed.");
    }
}
