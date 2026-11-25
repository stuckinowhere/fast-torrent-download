using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using FastTorrentDownload.Models;
using FastTorrentDownload.Services;
using MonoTorrent;
using MonoTorrent.BEncoding;
using MonoTorrent.Client;

/// <summary>
/// Files the user did not select must not appear in the download folder. A piece shared with a
/// selected file still has to be downloaded whole; the part that belongs to a deselected file is
/// kept in the app cache, and moves into the folder only if that file is selected later.
/// </summary>
internal static class SkippedFilesCheck
{
    private const int PieceLength = 262_144;

    // Sizes chosen so that every deselected file shares a piece with a selected one, and note.txt
    // lies wholly inside such a piece (the case that used to be downloaded in full).
    private static readonly (string Name, int Length)[] Layout =
    [
        ("a.bin", 700_000), ("note.txt", 150), ("b.bin", 900_000), ("c.bin", 600_000), ("d.bin", 500_000), ("owned.bin", 300_000)
    ];

    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "fast-torrent-skipped-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            var seedFolder = Path.Combine(root, "seed");
            var first = await CreateAsync(root, seedFolder, "skip-first");
            var second = await CreateAsync(root, seedFolder, "skip-second");

            int FreePort()
            {
                using var probe = new TcpListener(IPAddress.Loopback, 0);
                probe.Start();
                var port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
                return port;
            }

            var appPort = FreePort();
            var paths = new AppPaths(Path.Combine(root, "app-state"));
            paths.EnsureDirectories();
            var skipped = Path.Combine(paths.CacheDirectory, "skipped");
            var download = Path.Combine(root, "download");
            var settings = new AppSettings
            {
                DefaultDownloadFolder = download, ListenPort = appPort, ListenAddress = "127.0.0.1",
                EnableDht = false, EnablePeerExchange = false, EnableLocalPeerDiscovery = false, EnablePortForwarding = false
            };
            var service = new TorrentEngineService(paths);
            await service.InitializeAsync(settings);
            using var seedEngine = new ClientEngine(new EngineSettingsBuilder
            {
                CacheDirectory = Path.Combine(root, "seed-cache"), DhtEndPoint = null, AllowLocalPeerDiscovery = false, AllowPortForwarding = false,
                ListenEndPoints = new Dictionary<string, IPEndPoint> { ["ipv4"] = new(IPAddress.Loopback, FreePort()) }
            }.ToSettings());

            async Task<TorrentManager> SeedAsync(string source)
            {
                var seed = await seedEngine.AddAsync(Torrent.Load(source), seedFolder, new TorrentSettingsBuilder
                {
                    AllowDht = false, AllowPeerExchange = false, CreateContainingDirectory = true
                }.ToSettings());
                await seed.HashCheckAsync(autoStart: true);
                while (seed.State != TorrentState.Seeding) await Task.Delay(25, timeout.Token);
                return seed;
            }

            Task DialAsync(TorrentManager seed) => seed.AddPeerAsync(new PeerInfo(new Uri($"ipv4://127.0.0.1:{appPort}")));

            // 1. Two of six files selected; one deselected file is already in the folder, complete.
            var folder = Path.Combine(download, "skip-first");
            Directory.CreateDirectory(folder);
            await File.WriteAllBytesAsync(Path.Combine(folder, "owned.bin"), first.Files["owned.bin"]);
            var ownedStamp = File.GetLastWriteTimeUtc(Path.Combine(folder, "owned.bin"));
            var seedFirst = await SeedAsync(first.Source);
            var preview = await service.PrepareAddAsync(first.Source, null, timeout.Token);
            await service.CommitAddAsync(preview, ["a.bin", "c.bin"], startImmediately: true);
            while ((await service.GetSnapshotsAsync()).Single().State is not ("Downloading" or "Seeding")) await Task.Delay(50, timeout.Token);
            await DialAsync(seedFirst);
            while ((await service.GetSnapshotsAsync()).Count > 0) await Task.Delay(100, timeout.Token);

            var found = Directory.GetFiles(folder).Select(Path.GetFileName).Order().ToArray();
            Require(found.SequenceEqual(["a.bin", "c.bin", "owned.bin"]), $"Only the selected files (and the one already owned) may be in the folder: {string.Join(", ", found)}");
            Require((await File.ReadAllBytesAsync(Path.Combine(folder, "a.bin"))).AsSpan().SequenceEqual(first.Files["a.bin"]) &&
                    (await File.ReadAllBytesAsync(Path.Combine(folder, "c.bin"))).AsSpan().SequenceEqual(first.Files["c.bin"]), "A selected file is corrupt.");
            Require((await File.ReadAllBytesAsync(Path.Combine(folder, "owned.bin"))).AsSpan().SequenceEqual(first.Files["owned.bin"]) &&
                    File.GetLastWriteTimeUtc(Path.Combine(folder, "owned.bin")) == ownedStamp, "A deselected file the user already had must be left untouched.");
            Require(!Directory.Exists(skipped) || !Directory.EnumerateFiles(skipped).Any(), "Hidden data must be removed once the torrent leaves the queue.");

            // 2. One file selected; the rest is selected later, after an app restart, a recheck and a move.
            var seedSecond = await SeedAsync(second.Source);
            preview = await service.PrepareAddAsync(second.Source, null, timeout.Token);
            await service.CommitAddAsync(preview, ["a.bin"], startImmediately: true);
            var hold = new HashSet<string> { preview.Id }; // Keeps the finished torrent in the queue for the next steps.
            while ((await service.GetSnapshotsAsync(hold)).Single().State is not ("Downloading" or "Seeding")) await Task.Delay(50, timeout.Token);
            await DialAsync(seedSecond);
            while ((await service.GetSnapshotsAsync(hold)).Single().VerifiedBytes < first.Files["a.bin"].Length) await Task.Delay(100, timeout.Token);
            folder = Path.Combine(download, "skip-second");
            Require(Directory.GetFiles(folder).Select(Path.GetFileName).SequenceEqual(["a.bin"]), $"Only a.bin may be in the folder: {string.Join(", ", Directory.GetFiles(folder).Select(Path.GetFileName))}");
            Require(Directory.EnumerateFiles(skipped, "*.part").Any(), "The bytes shared with a.bin must be kept as hidden data.");

            // Restart with the torrent paused (a running, finished torrent would leave the queue at once).
            await service.PauseAsync(preview.Id);
            await service.DisposeAsync();
            service = new TorrentEngineService(paths);
            await service.InitializeAsync(settings);
            var restored = (await service.GetSnapshotsAsync()).Single();
            Require(restored.State == "Stopped" && Directory.EnumerateFiles(skipped, "*.part").Any(), $"The restart must keep the paused torrent and its hidden data: {restored.State}.");
            var id = restored.Id;
            hold = [id];
            // A recheck reads the shared piece back from the hidden data; without it a.bin would lose that piece.
            await service.RecheckAsync(id);
            while ((await service.GetSnapshotsAsync(hold)).Single().State is not ("Downloading" or "Seeding")) await Task.Delay(50, timeout.Token);
            var rechecked = (await service.GetSnapshotsAsync(hold)).Single();
            Require(rechecked.VerifiedBytes >= first.Files["a.bin"].Length, $"The recheck lost data of the selected file: {rechecked.VerifiedBytes} bytes verified.");
            Require(Directory.GetFiles(folder).Select(Path.GetFileName).SequenceEqual(["a.bin"]), "A recheck must not bring deselected files into the folder.");

            var moved = Path.Combine(root, "moved");
            await service.MoveAsync(id, moved);
            // A move places the torrent's files directly in the chosen folder.
            folder = moved;
            var afterMove = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .Where(path => path.StartsWith(moved) || path.StartsWith(Path.Combine(download, "skip-second")))
                .Select(path => Path.GetRelativePath(root, path)).Order().ToArray();
            Require(afterMove.SequenceEqual([Path.Combine("moved", "a.bin")]) && Directory.EnumerateFiles(skipped, "*.part").Any(),
                $"Moving the torrent must move a.bin only and keep the hidden data: {string.Join(", ", afterMove)}");

            await service.SetSelectedFilesAsync(id, ["a.bin", "note.txt", "b.bin"]);
            var wanted = first.Files["a.bin"].Length + first.Files["note.txt"].Length + first.Files["b.bin"].Length;
            while ((await service.GetSnapshotsAsync(hold)).Single().State is not ("Downloading" or "Seeding")) await Task.Delay(50, timeout.Token);
            await DialAsync(seedSecond);
            while ((await service.GetSnapshotsAsync(hold)).Single().VerifiedBytes < wanted) await Task.Delay(100, timeout.Token);
            while ((await service.GetSnapshotsAsync()).Count > 0) await Task.Delay(100, timeout.Token);

            found = Directory.GetFiles(folder).Select(Path.GetFileName).Order().ToArray();
            Require(found.SequenceEqual(["a.bin", "b.bin", "note.txt"]), $"After selecting two more files the folder holds: {string.Join(", ", found)}");
            foreach (var name in found)
            {
                Require((await File.ReadAllBytesAsync(Path.Combine(folder, name!))).AsSpan().SequenceEqual(second.Files[name!]), $"{name} is corrupt after being selected later.");
            }

            Require(!Directory.EnumerateFiles(skipped).Any(), "Hidden data must be removed once the torrent leaves the queue.");
            await service.DisposeAsync();
            Console.WriteLine("PASS deselected files stay out of the download folder: shared pieces are kept as hidden data across restart, recheck and move, an owned file is untouched, and selecting a file later completes it intact");
        }
        finally
        {
            timeout.Cancel();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static async Task<(string Source, Dictionary<string, byte[]> Files)> CreateAsync(string root, string seedFolder, string name)
    {
        var files = Layout.ToDictionary(file => file.Name, file => RandomNumberGenerator.GetBytes(file.Length));
        var data = Layout.SelectMany(file => files[file.Name]).ToArray();
        var hashes = Enumerable.Range(0, (data.Length + PieceLength - 1) / PieceLength)
            .SelectMany(index => SHA1.HashData(data.AsSpan(index * PieceLength, Math.Min(PieceLength, data.Length - index * PieceLength)))).ToArray();
        var list = new BEncodedList();
        foreach (var file in Layout)
        {
            list.Add(new BEncodedDictionary { ["length"] = new BEncodedNumber(file.Length), ["path"] = new BEncodedList { new BEncodedString(file.Name) } });
        }

        var source = Path.Combine(root, name + ".torrent");
        await File.WriteAllBytesAsync(source, new BEncodedDictionary
        {
            ["info"] = new BEncodedDictionary
            {
                ["name"] = new BEncodedString(name), ["files"] = list,
                ["piece length"] = new BEncodedNumber(PieceLength), ["pieces"] = new BEncodedString(hashes)
            }
        }.Encode());
        Directory.CreateDirectory(Path.Combine(seedFolder, name));
        foreach (var file in Layout)
        {
            await File.WriteAllBytesAsync(Path.Combine(seedFolder, name, file.Name), files[file.Name]);
        }

        return (source, files);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
