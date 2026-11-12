using System.Net;
using System.Collections.Concurrent;
using FastTorrentDownload.Models;
using MonoTorrent;
using MonoTorrent.BEncoding;
using MonoTorrent.Client;
using MonoTorrent.Connections;
using MonoTorrent.Trackers;

namespace FastTorrentDownload.Services;

// Default, restored and user-added trackers.
public sealed partial class TorrentEngineService
{
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

    // Caller holds _gate.
    private void RememberTrackers(TorrentManager manager, IEnumerable<string> urls)
    {
        var hash = DescribeInfoHashes(manager.InfoHashes);
        if (!_extraTrackers.TryGetValue(hash, out var known))
        {
            _extraTrackers[hash] = known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        foreach (var url in urls)
        {
            if (known.Count >= MaxExtraTrackersPerTorrent) break;
            known.Add(url);
        }
    }

    private async Task RestoreExtraTrackersAsync(TorrentManager manager)
    {
        var hash = DescribeInfoHashes(manager.InfoHashes);
        if (!_extraTrackers.TryGetValue(hash, out var recorded))
        {
            // Queues saved before trackers were persisted: a torrent left with a single tracker
            // gets the same public rescue set a new magnet does.
            if (!_stateRecordsTrackers && CountTrackers(manager) < 2)
            {
                await SeedDefaultTrackersAsync(manager);
                RememberTrackers(manager, DefaultPublicTrackers.Select(uri => uri.AbsoluteUri));
            }

            return;
        }

        var existing = manager.TrackerManager.Tiers.SelectMany(tier => tier.Trackers).Select(tracker => tracker.Uri).ToHashSet();
        foreach (var url in recorded)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && existing.Add(uri))
            {
                try { await manager.TrackerManager.AddTrackerAsync(uri); }
                catch (Exception exception) { AppLogger.Log($"Saved tracker skipped ({exception.GetType().Name})."); }
            }
        }
    }

    /// <summary>
    /// Splits pasted text into tracker URLs. Only udp/http/https announce URLs with a host are
    /// accepted; everything else is returned as rejected so the dialog can say what was ignored.
    /// </summary>
    public static (IReadOnlyList<Uri> Accepted, IReadOnlyList<string> Rejected) ParseTrackerUrls(string? text)
    {
        var accepted = new List<Uri>();
        var rejected = new List<string>();
        foreach (var token in (text ?? string.Empty).Split([' ', '\t', '\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.Length <= 2048 && Uri.TryCreate(token, UriKind.Absolute, out var uri) &&
                uri.Scheme is "udp" or "http" or "https" && !string.IsNullOrWhiteSpace(uri.Host))
            {
                if (!accepted.Contains(uri) && accepted.Count < MaxExtraTrackersPerTorrent) accepted.Add(uri);
            }
            else
            {
                rejected.Add(token.Length > 80 ? token[..80] + "…" : token);
            }
        }

        return (accepted, rejected);
    }

    /// <summary>Adds trackers to a queued torrent, announces to them and persists them. Returns how many were new.</summary>
    public async Task<int> AddTrackersAsync(string id, IReadOnlyCollection<Uri> trackers)
    {
        var added = 0;
        TorrentManager manager;
        await _gate.WaitAsync();
        try
        {
            manager = Find(id);
            var existing = manager.TrackerManager.Tiers.SelectMany(tier => tier.Trackers).Select(tracker => tracker.Uri).ToHashSet();
            var fresh = new List<string>();
            foreach (var uri in trackers)
            {
                if (!existing.Add(uri)) continue;
                await manager.TrackerManager.AddTrackerAsync(uri);
                fresh.Add(uri.AbsoluteUri);
                added++;
            }

            if (added > 0)
            {
                RememberTrackers(manager, fresh);
                await SaveStateAsync();
                _lastScrape.TryRemove(id, out _);
            }
        }
        finally
        {
            _gate.Release();
        }

        if (added > 0 && manager.State is TorrentState.Downloading or TorrentState.Seeding or TorrentState.Metadata)
        {
            _ = TriggerImmediateAnnouncesAsync(manager);
        }

        return added;
    }
}
