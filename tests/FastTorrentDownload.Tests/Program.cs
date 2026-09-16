using FastTorrentDownload.Models;
using FastTorrentDownload.Services;
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

Console.WriteLine($"PASS {passed} focused checks");
return 0;

void Check(string name, Action action)
{
    action();
    passed++;
    Console.WriteLine($"PASS {name}");
}

static TorrentSnapshot Snapshot(double progress, long downloaded, long uploaded, string state = "Seeding") => new(
    "test", "test", "test", state, progress, 0, 0, downloaded, uploaded, progress >= 100);

static void Require(bool condition)
{
    if (!condition)
    {
        throw new InvalidOperationException("Assertion failed.");
    }
}
