using System.Net;
using System.Collections.Concurrent;
using FastTorrentDownload.Models;
using MonoTorrent;
using MonoTorrent.BEncoding;
using MonoTorrent.Client;
using MonoTorrent.Connections;
using MonoTorrent.Trackers;

namespace FastTorrentDownload.Services;

// Magnet metadata resolution: swarm download with tracker rotation and the opt-in public caches.
public sealed partial class TorrentEngineService
{
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
            onProgress?.Invoke(Loc.I($"Fetching torrent file list… ({elapsed}s / 60s)"),
                Loc.T("Payload files will only download after you confirm the selection."));
            await Task.Delay(1000, token);
            elapsed++;
        }
    }

    public static bool TryCacheMagnetTorrentBytes(MagnetLink magnet, byte[] torrentBytes, out Torrent? verifiedTorrent)
    {
        verifiedTorrent = null;
        var hash = magnet.InfoHashes.V1?.ToHex().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(hash) ||
            torrentBytes is not { Length: > 32 and < MaxCachedTorrentBytes } ||
            !Torrent.TryLoad(torrentBytes, out var loaded) ||
            loaded.InfoHashes != magnet.InfoHashes)
        {
            return false;
        }

        // Only the info dictionary is covered by the info-hash check. Announce lists and
        // webseeds supplied by a cache host are untrusted, so they never reach the engine.
        var infoOnly = new BEncodedDictionary
        {
            ["info"] = BEncodedValue.Decode<BEncodedDictionary>(torrentBytes)["info"]
        }.Encode();
        if (!Torrent.TryLoad(infoOnly, out var stripped) || stripped.InfoHashes != magnet.InfoHashes)
        {
            return false;
        }

        CachedMagnetTorrents[hash] = infoOnly;
        verifiedTorrent = stripped;
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
        RememberTrackers(manager, DefaultPublicTrackers.Select(uri => uri.AbsoluteUri));
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
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5),
            MaxResponseContentBufferSize = MaxCachedTorrentBytes
        };
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
}
