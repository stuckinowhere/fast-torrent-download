using System.Net;
using System.Collections.Concurrent;
using FastTorrentDownload.Models;
using MonoTorrent;
using MonoTorrent.BEncoding;
using MonoTorrent.Client;
using MonoTorrent.Connections;
using MonoTorrent.Trackers;

namespace FastTorrentDownload.Services;

// Active-download limit: starting, parking and promoting torrents.
public sealed partial class TorrentEngineService
{
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

    // A stalled torrent transfers nothing, so it must not keep queued torrents waiting.
    private int CountActiveDownloads() =>
        _entries.Count(entry => IsActiveState(entry.Value.Manager.State) && !_stalledIds.ContainsKey(entry.Key));

    public static bool IsActiveState(TorrentState state) =>
        state is TorrentState.Downloading or TorrentState.Metadata or TorrentState.Hashing
            or TorrentState.Starting or TorrentState.Seeding or TorrentState.FetchingHashes;

    public static bool ShouldQueue(int maximumActiveDownloads, int activeDownloads) =>
        maximumActiveDownloads > 0 && activeDownloads >= maximumActiveDownloads;
}
