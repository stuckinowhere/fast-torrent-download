using System.Net;
using FastTorrentDownload.Services;
using MonoTorrent;
using MonoTorrent.BEncoding;
using MonoTorrent.Client;

/// <summary>
/// Joins a live swarm from outside the app with the app's own connection settings and reports,
/// per peer, who is choking whom and whether any payload arrives. It answers "is the swarm
/// refusing to send, or is the client failing to ask?". Addresses are never printed.
/// Usage: --swarm-probe &lt;file.torrent&gt; [--seconds 90] [--have &lt;fastresume file&gt;]
/// </summary>
internal static class SwarmProbe
{
    public static async Task<int> RunAsync(string torrentPath, int seconds, string? fastResumePath)
    {
        var root = Path.Combine(Path.GetTempPath(), "fast-torrent-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var torrent = Torrent.Load(torrentPath);
            bool[]? have = null;
            if (fastResumePath is not null && File.Exists(fastResumePath))
            {
                var resume = BEncodedValue.Decode<BEncodedDictionary>(await File.ReadAllBytesAsync(fastResumePath));
                var bits = ((BEncodedString)resume["bitfield"]).Span.ToArray();
                have = Enumerable.Range(0, torrent.PieceCount).Select(index => (bits[index / 8] & (0x80 >> (index % 8))) != 0).ToArray();
            }

            using var engine = new ClientEngine(new EngineSettingsBuilder
            {
                CacheDirectory = Path.Combine(root, "cache"),
                AllowLocalPeerDiscovery = false,
                AllowPortForwarding = false,
                AllowedEncryption = EnginePerformanceTuning.GetAllowedEncryption(requireEncrypted: false).ToList(),
                ConnectionTimeout = EnginePerformanceTuning.ConnectionTimeout,
                MaximumConnections = 200,
                MaximumHalfOpenConnections = EnginePerformanceTuning.MaxHalfOpenConnections,
                // A trickle is enough to see whether anyone sends; this is a probe, not a download.
                MaximumDownloadRate = 8 * 1024,
                MaximumUploadRate = 1024
            }.ToSettings());
            var manager = await engine.AddAsync(torrent, Path.Combine(root, "data"), new TorrentSettingsBuilder
            {
                AllowDht = true, AllowPeerExchange = true, MaximumConnections = 120, UploadSlots = 1
            }.ToSettings());
            foreach (var uri in TorrentEngineService.DefaultPublicTrackers)
            {
                try { await manager.TrackerManager.AddTrackerAsync(uri); } catch { }
            }

            await manager.StartAsync();
            Console.WriteLine($"Probe: {torrent.PieceCount} pieces of {torrent.PieceLength / 1024} KiB; local copy has {(have is null ? "unknown" : have.Count(bit => bit) + " pieces")}. Watching for {seconds}s…");
            var everUnchoked = 0;
            for (var elapsed = 10; elapsed <= seconds; elapsed += 10)
            {
                await Task.Delay(TimeSpan.FromSeconds(10));
                var peers = (await manager.GetPeersAsync()).Where(peer => peer.IsConnected).ToArray();
                var unchoking = peers.Count(peer => !peer.IsChoking);
                everUnchoked = Math.Max(everUnchoked, unchoking);
                Console.WriteLine($"t={elapsed,3}s state={manager.State} known={manager.Peers.Available + peers.Length} connected={peers.Length} " +
                                  $"claimSeeder={peers.Count(peer => peer.IsSeeder)} weAreInterested={peers.Count(peer => peer.AmInterested)} " +
                                  $"unchokingUs={unchoking} sending={peers.Count(peer => peer.Monitor.DownloadRate > 0)} received={manager.Monitor.DataBytesReceived / 1024} KiB");
            }

            var final = (await manager.GetPeersAsync()).Where(peer => peer.IsConnected).ToArray();
            Console.WriteLine("Per peer (no addresses): client | direction | encryption | pieces | claims seeder | we interested | choking us | received");
            foreach (var peer in final.OrderByDescending(peer => peer.BitField.TrueCount).Take(40))
            {
                Console.WriteLine($"  {peer.ClientApp.Client,-14} | {peer.ConnectionDirection,-8} | {peer.EncryptionType,-9} | {peer.BitField.TrueCount,4}/{torrent.PieceCount} | " +
                                  $"{(peer.IsSeeder ? "yes" : "no "),-3} | {(peer.AmInterested ? "yes" : "no "),-3} | {(peer.IsChoking ? "yes" : "no "),-3} | {peer.Monitor.DataBytesReceived / 1024} KiB");
            }

            if (have is not null)
            {
                var missing = Enumerable.Range(0, torrent.PieceCount).Where(index => !have[index]).ToArray();
                var partial = final.Where(peer => !peer.IsSeeder).ToArray();
                var covered = missing.Count(index => partial.Any(peer => peer.BitField.Length > index && peer.BitField[index]));
                Console.WriteLine($"Of the {missing.Length} pieces the local copy lacks, {covered} are held by connected peers that do not claim to be seeders.");
            }

            Console.WriteLine($"Verdict: received {manager.Monitor.DataBytesReceived / 1024} KiB; at most {everUnchoked} peer(s) unchoked the probe at once; {final.Count(peer => peer.IsSeeder)} connected peer(s) claim to be seeders.");
            await manager.StopAsync(TimeSpan.FromSeconds(2));
            return 0;
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
