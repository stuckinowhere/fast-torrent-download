using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using FastTorrentDownload.Models;
using FastTorrentDownload.Services;
using FastTorrentDownload.ViewModels;
using MonoTorrent;
using MonoTorrent.BEncoding;
using MonoTorrent.Client;

/// <summary>
/// Manual check, run with --telegram-live: completes a small loopback download through the real
/// view model and lets it send its completion alert to the bot saved in the installed profile.
/// Only the phone-alert settings are borrowed from that profile; the queue, the download folder
/// and everything else live in a temporary folder. The bot token is never printed.
/// </summary>
internal static class TelegramLiveCheck
{
    public static async Task<int> RunAsync()
    {
        var installed = await new SettingsStore(new AppPaths()).LoadAsync();
        if (!installed.TelegramEnabled || !TelegramNotifier.IsValidToken(SecretProtector.Unprotect(installed.TelegramBotToken)) ||
            !TelegramNotifier.IsValidChat(installed.TelegramChatId))
        {
            Console.WriteLine("Phone alerts are not set up in the installed profile: enable them in Settings, press Send test, then Save settings.");
            return 2;
        }

        Loc.Use(Loc.Resolve(installed.Language));
        var root = Path.Combine(Path.GetTempPath(), "fast-torrent-telegram-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            const int pieceLength = 16_384;
            var name = $"Prueba de aviso {DateTime.Now:HH.mm.ss}.bin";
            var data = RandomNumberGenerator.GetBytes(300_123);
            var hashes = Enumerable.Range(0, (data.Length + pieceLength - 1) / pieceLength)
                .SelectMany(index => SHA1.HashData(data.AsSpan(index * pieceLength, Math.Min(pieceLength, data.Length - index * pieceLength)))).ToArray();
            var source = Path.Combine(root, "live.torrent");
            await File.WriteAllBytesAsync(source, new BEncodedDictionary
            {
                ["info"] = new BEncodedDictionary
                {
                    ["name"] = new BEncodedString(name), ["length"] = new BEncodedNumber(data.Length),
                    ["piece length"] = new BEncodedNumber(pieceLength), ["pieces"] = new BEncodedString(hashes)
                }
            }.Encode());
            var seedFolder = Path.Combine(root, "seed");
            Directory.CreateDirectory(seedFolder);
            await File.WriteAllBytesAsync(Path.Combine(seedFolder, name), data);

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
            await new SettingsStore(paths).SaveAsync(new AppSettings
            {
                DefaultDownloadFolder = Path.Combine(root, "download"), ListenPort = appPort, ListenAddress = "127.0.0.1",
                EnableDht = false, EnablePeerExchange = false, EnableLocalPeerDiscovery = false, EnablePortForwarding = false,
                Language = installed.Language, ConfirmCloseBeforeExit = false,
                TelegramEnabled = true, TelegramBotToken = installed.TelegramBotToken, TelegramChatId = installed.TelegramChatId,
                TelegramNotifyProblems = installed.TelegramNotifyProblems
            });
            var service = new TorrentEngineService(paths);
            var viewModel = new MainViewModel(service, new SettingsStore(paths), new ReleaseUpdateService());
            await viewModel.InitializeAsync();

            using var seedEngine = new ClientEngine(new EngineSettingsBuilder
            {
                CacheDirectory = Path.Combine(root, "seed-cache"), DhtEndPoint = null, AllowLocalPeerDiscovery = false, AllowPortForwarding = false,
                ListenEndPoints = new Dictionary<string, IPEndPoint> { ["ipv4"] = new(IPAddress.Loopback, FreePort()) }
            }.ToSettings());
            var seed = await seedEngine.AddAsync(Torrent.Load(source), seedFolder, new TorrentSettingsBuilder
            {
                AllowDht = false, AllowPeerExchange = false, CreateContainingDirectory = false
            }.ToSettings());
            await seed.HashCheckAsync(autoStart: true);
            while (seed.State != TorrentState.Seeding) await Task.Delay(25, timeout.Token);

            var preview = await viewModel.PrepareAddAsync(source, null, timeout.Token);
            await viewModel.AddAsync(preview, [name], startImmediately: true);
            while ((await service.GetSnapshotsAsync(new HashSet<string> { preview.Id })).Single().State is not ("Downloading" or "Seeding")) await Task.Delay(50, timeout.Token);
            await seed.AddPeerAsync(new PeerInfo(new Uri($"ipv4://127.0.0.1:{appPort}")));

            // The same loop the window runs once a second: a finished torrent leaves the queue and raises its notices.
            do
            {
                await Task.Delay(200, timeout.Token);
                await viewModel.RefreshAsync(waitForPending: true);
            }
            while (viewModel.Torrents.Count > 0);
            await viewModel.PendingPhoneAlert.WaitAsync(timeout.Token);

            var failure = viewModel.Activity.Select(entry => entry.Message)
                .FirstOrDefault(message => message.StartsWith(Loc.I($"Telegram alert not sent: {string.Empty}"), StringComparison.Ordinal));
            var intact = (await File.ReadAllBytesAsync(Path.Combine(root, "download", name))).AsSpan().SequenceEqual(data);
            await viewModel.ShutdownAsync();
            if (failure is not null || !intact)
            {
                Console.WriteLine($"FAIL download intact={intact}; {failure ?? "alert reported as sent"}");
                return 1;
            }

            Console.WriteLine($"PASS a real download completed and Telegram accepted the alert: {Loc.I($"Download complete: {name}")}");
            return 0;
        }
        finally
        {
            timeout.Cancel();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
