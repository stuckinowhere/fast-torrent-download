using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using FastTorrentDownload.Models;
using FastTorrentDownload.Services;
using FastTorrentDownload.ViewModels;
using MonoTorrent;
using MonoTorrent.BEncoding;
using MonoTorrent.Client;

internal static class LiveTransferCheck
{
    public static async Task RunAsync(bool complete)
    {
        var root = Path.Combine(Path.GetTempPath(), "fast-torrent-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        using var tracker = new TcpListener(IPAddress.Loopback, 0);
        tracker.Start();
        var trackerUri = new Uri($"http://127.0.0.1:{((IPEndPoint)tracker.LocalEndpoint).Port}/announce");
        var clients = new List<TcpClient>();
        var server = HoldTrackerRequestsAsync();
        try
        {
            const int pieceLength = 16_384;
            var data = RandomNumberGenerator.GetBytes(complete ? 1_000_123 : 4_000_123);
            var hashes = Enumerable.Range(0, (data.Length + pieceLength - 1) / pieceLength)
                .SelectMany(index => SHA1.HashData(data.AsSpan(index * pieceLength, Math.Min(pieceLength, data.Length - index * pieceLength)))).ToArray();
            var encoded = new BEncodedDictionary
            {
                ["info"] = new BEncodedDictionary
                {
                    ["name"] = new BEncodedString("loopback.bin"), ["length"] = new BEncodedNumber(data.Length),
                    ["piece length"] = new BEncodedNumber(pieceLength), ["pieces"] = new BEncodedString(hashes)
                }
            };
            var source = Path.Combine(root, "loopback.torrent");
            await File.WriteAllBytesAsync(source, encoded.Encode());
            var seedFolder = Path.Combine(root, "seed");
            Directory.CreateDirectory(seedFolder);
            await File.WriteAllBytesAsync(Path.Combine(seedFolder, "loopback.bin"), data);
            using var port = new TcpListener(IPAddress.Loopback, 0);
            port.Start();
            var seedPort = ((IPEndPoint)port.LocalEndpoint).Port;
            port.Stop();
            using var seedEngine = new ClientEngine(Settings(Path.Combine(root, "seed-cache"), seedPort));
            var seed = await seedEngine.AddAsync(Torrent.Load(source), seedFolder, new TorrentSettingsBuilder
            {
                AllowDht = false, AllowPeerExchange = false, CreateContainingDirectory = false
            }.ToSettings());
            await seed.HashCheckAsync(autoStart: true);
            await UntilAsync(() => seed.State == TorrentState.Seeding);

            var paths = new AppPaths(Path.Combine(root, "download-state"));
            paths.EnsureDirectories();
            using var downloadEngine = new ClientEngine(Settings(paths.CacheDirectory, null, 128 * 1024));
            var service = new TorrentEngineService(paths);
            typeof(TorrentEngineService).GetField("_engine", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, downloadEngine);
            var preview = await service.PrepareAddAsync(source, Path.Combine(root, "download"));
            await service.CommitAddAsync(preview, ["loopback.bin"], startImmediately: false);
            var manager = downloadEngine.Torrents.Single();
            var downloadedPath = manager.Files.Single().FullPath;
            var viewModel = new MainViewModel(service, new SettingsStore(paths), new ReleaseUpdateService());
            await viewModel.RefreshAsync();
            viewModel.SelectedTorrent = viewModel.Torrents.Single();
            await viewModel.StartSelectedAsync();
            await UntilAsync(() => manager.State is TorrentState.Downloading or TorrentState.Seeding);
            await viewModel.RefreshAsync();
            await manager.AddPeerAsync(new PeerInfo(new Uri($"ipv4://127.0.0.1:{seedPort}")));
            await UntilAsync(() => TorrentEngineService.GetSelectedBytes(manager).Verified >= pieceLength);
            await manager.TrackerManager.AddTrackerAsync(trackerUri);
            var row = viewModel.SelectedTorrent;
            var watch = Stopwatch.StartNew();
            var pause = viewModel.PauseTorrentAsync(preview.Id);
            Require(row.IsBusy && !row.CanAct, "Pause did not immediately mark the row busy.");
            await UntilAsync(() => manager.State == TorrentState.Stopping);
            await viewModel.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(1));
            Require(row.StateLabel == "Stopping…", "The row did not keep its operation state during refresh.");
            await viewModel.PauseTorrentAsync(preview.Id); // Duplicate input must be ignored.
            await pause.WaitAsync(TimeSpan.FromSeconds(8));
            Require(watch.Elapsed < TimeSpan.FromSeconds(8) && manager.State == TorrentState.Stopped, $"Pause did not finish correctly: {manager.State}, elapsed={watch.Elapsed}.");
            Require(clients.Count > 0, "The stalled tracker was not exercised.");
            Require(File.Exists(downloadedPath), "Pause lost the downloaded file.");

            await viewModel.StartSelectedAsync();
            await UntilAsync(() => manager.State is TorrentState.Downloading or TorrentState.Seeding);
            await manager.AddPeerAsync(new PeerInfo(new Uri($"ipv4://127.0.0.1:{seedPort}")));
            if (complete)
            {
                while (viewModel.Torrents.Count > 0)
                {
                    await Task.Delay(50, timeout.Token);
                    await viewModel.RefreshAsync();
                }
                Require((await File.ReadAllBytesAsync(downloadedPath)).SequenceEqual(data), "The completed transfer is corrupt.");
                Require(viewModel.Notification?.Message.StartsWith("Completed") == true, "The completion notification is missing.");
            }
            else
            {
                await UntilAsync(() => TorrentEngineService.GetSelectedBytes(manager).Verified >= pieceLength * 2);
                await viewModel.RefreshAsync();
                watch.Restart();
                var removal = viewModel.RemoveSelectedAsync();
                await UntilAsync(() => manager.State == TorrentState.Stopping);
                await viewModel.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(1));
                await viewModel.RemoveSelectedAsync(); // Repeated clicks must not enqueue more work.
                await removal.WaitAsync(TimeSpan.FromSeconds(8));
                Require(watch.Elapsed < TimeSpan.FromSeconds(8) && viewModel.Torrents.Count == 0, "The tracker blocked removal.");
                Require(File.Exists(downloadedPath), "Removal deleted the payload.");
                using var disk = File.OpenRead(downloadedPath);
                for (var piece = 0; piece < manager.Bitfield.Length; piece++)
                {
                    if (!manager.Bitfield[piece]) continue;
                    var buffer = new byte[Math.Min(pieceLength, data.Length - piece * pieceLength)];
                    disk.Position = piece * pieceLength;
                    await disk.ReadExactlyAsync(buffer);
                    Require(buffer.AsSpan().SequenceEqual(data.AsSpan(piece * pieceLength, buffer.Length)), "A verified piece was damaged.");
                }
            }
            Require(((BEncodedList)BEncodedValue.Decode<BEncodedDictionary>(await File.ReadAllBytesAsync(paths.EngineStateFile))["Torrents"]).Count == 0, "Removal was not persisted.");
            Console.WriteLine($"PASS live loopback transfer, stalled-tracker pause and {(complete ? "automatic completion" : "manual removal")}, responsive refresh and file integrity");
        }
        finally
        {
            timeout.Cancel();
            tracker.Stop();
            await server;
            foreach (var client in clients) client.Dispose();
            Directory.Delete(root, recursive: true);
        }

        async Task HoldTrackerRequestsAsync()
        {
            try
            {
                while (!timeout.IsCancellationRequested) clients.Add(await tracker.AcceptTcpClientAsync(timeout.Token));
            }
            catch (OperationCanceledException) { }
            catch (SocketException) when (timeout.IsCancellationRequested) { }
        }
        async Task UntilAsync(Func<bool> condition)
        {
            while (!condition()) await Task.Delay(25, timeout.Token);
        }
    }

    private static EngineSettings Settings(string cache, int? listenPort, int downloadLimit = 0) => new EngineSettingsBuilder
    {
        CacheDirectory = cache, DhtEndPoint = null, AllowLocalPeerDiscovery = false, AllowPortForwarding = false,
        MaximumDownloadRate = downloadLimit,
        ListenEndPoints = listenPort is { } port ? new Dictionary<string, IPEndPoint> { ["ipv4"] = new(IPAddress.Loopback, port) } : new()
    }.ToSettings();

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
