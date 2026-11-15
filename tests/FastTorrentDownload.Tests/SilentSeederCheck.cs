using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using FastTorrentDownload.Models;
using FastTorrentDownload.Services;
using FastTorrentDownload.ViewModels;
using MonoTorrent;
using MonoTorrent.BEncoding;

/// <summary>
/// A hand-rolled BitTorrent peer dials the app, advertises every piece and then never unchokes.
/// Seen in a real dead swarm: such peers made the app report "100% coverage" and hide that no
/// reachable peer held the missing data.
/// </summary>
internal static class SilentSeederCheck
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "fast-torrent-silent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var threshold = TorrentEngineService.StallThreshold;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            const int pieceLength = 16_384;
            const int pieces = 64;
            var data = RandomNumberGenerator.GetBytes(pieceLength * pieces);
            var hashes = Enumerable.Range(0, pieces).SelectMany(index => SHA1.HashData(data.AsSpan(index * pieceLength, pieceLength))).ToArray();
            var source = Path.Combine(root, "silent.torrent");
            await File.WriteAllBytesAsync(source, new BEncodedDictionary
            {
                ["info"] = new BEncodedDictionary
                {
                    ["name"] = new BEncodedString("silent.bin"), ["length"] = new BEncodedNumber(data.Length),
                    ["piece length"] = new BEncodedNumber(pieceLength), ["pieces"] = new BEncodedString(hashes)
                }
            }.Encode());
            var infoHash = Torrent.Load(source).InfoHashes.V1!.Span.ToArray();

            using var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            var paths = new AppPaths(Path.Combine(root, "app-state"));
            paths.EnsureDirectories();
            await using var service = new TorrentEngineService(paths);
            await service.InitializeAsync(new AppSettings
            {
                DefaultDownloadFolder = Path.Combine(root, "download"), ListenPort = port, ListenAddress = "127.0.0.1",
                EnableDht = false, EnablePeerExchange = false, EnableLocalPeerDiscovery = false, EnablePortForwarding = false
            });
            var preview = await service.PrepareAddAsync(source, null, timeout.Token);
            await service.CommitAddAsync(preview, ["silent.bin"], startImmediately: true);
            while ((await service.GetSnapshotsAsync()).Single().State != "Downloading") await Task.Delay(50, timeout.Token);

            using var peer = new TcpClient(AddressFamily.InterNetwork);
            await peer.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            var stream = peer.GetStream();
            // Handshake: <19>"BitTorrent protocol" <8 reserved> <info-hash> <peer id>.
            var handshake = new byte[68];
            handshake[0] = 19;
            Encoding.ASCII.GetBytes("BitTorrent protocol").CopyTo(handshake, 1);
            infoHash.CopyTo(handshake, 28);
            Encoding.ASCII.GetBytes("-XX0001-silentseeder").CopyTo(handshake, 48);
            await stream.WriteAsync(handshake, timeout.Token);
            await stream.ReadExactlyAsync(new byte[68], timeout.Token);
            // Bitfield (id 5) with every piece set; this peer stays in its initial choking state for good.
            var bitfield = new byte[4 + 1 + pieces / 8];
            bitfield[3] = (byte)(1 + pieces / 8);
            bitfield[4] = 5;
            Array.Fill(bitfield, (byte)0xFF, 5, pieces / 8);
            await stream.WriteAsync(bitfield, timeout.Token);
            var drain = Task.Run(async () =>
            {
                var buffer = new byte[4096];
                try { while (await stream.ReadAsync(buffer, timeout.Token) > 0) { } }
                catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException) { }
            });

            TorrentSnapshot snapshot;
            do
            {
                await Task.Delay(100, timeout.Token);
                snapshot = (await service.GetSnapshotsAsync()).Single();
            }
            while (snapshot.Diagnostics?.SilentSeeders != 1);
            Require(snapshot.Diagnostics!.ConnectedPeers == 1 && snapshot.Diagnostics.ConnectedSeeders == 0, "A peer that only claims the pieces must not count as a seeder.");
            Require(snapshot.Diagnostics.AvailablePiecesPercent == 0, $"Its pieces must not count as coverage: {snapshot.Diagnostics.AvailablePiecesPercent}.");

            TorrentEngineService.StallThreshold = TimeSpan.FromMilliseconds(300);
            do
            {
                await Task.Delay(100, timeout.Token);
                snapshot = (await service.GetSnapshotsAsync()).Single();
            }
            while (snapshot.State != "Stalled" || snapshot.Diagnostics?.SilentSeeders != 1);
            var reason = $"Connected peers hold only {0.0:0.0}% of the data · 1 peer claims the full file but sends nothing";
            Require(snapshot.Diagnostics!.StallSummary == reason, $"Unexpected stall reason: {snapshot.Diagnostics.StallSummary}");
            Require(new TorrentRowViewModel(snapshot).DetailLabel.EndsWith(reason, StringComparison.Ordinal), "The row must show the reason.");
            TorrentEngineService.StallThreshold = threshold;
            peer.Close();
            await drain;
            await service.RemoveAsync(snapshot.Id);
            Console.WriteLine("PASS a peer that claims every piece but never sends is named, and is not counted as a seeder or as coverage");
        }
        finally
        {
            TorrentEngineService.StallThreshold = threshold;
            timeout.Cancel();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
