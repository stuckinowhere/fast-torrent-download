using FastTorrentDownload.Models;

namespace FastTorrentDownload.Services;

public static class QueueCoordinator
{
    public static bool ShouldPauseForRatio(TorrentSnapshot snapshot, AppSettings settings) =>
        snapshot.IsComplete &&
        snapshot.State.Contains("Seeding", StringComparison.OrdinalIgnoreCase) &&
        settings.SeedRatioTarget > 0 &&
        snapshot.Ratio >= settings.SeedRatioTarget;

}
