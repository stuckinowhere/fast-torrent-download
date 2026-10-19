using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FastTorrentDownload.Models;
using FastTorrentDownload.Services;
using FastTorrentDownload.ViewModels;
using FastTorrentDownload.Views;
using MonoTorrent;
using MonoTorrent.BEncoding;
using MonoTorrent.Client;

internal static class UiSmoke
{
    [STAThread]
    public static int Run(bool automated = false)
    {
        var result = 0;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var paths = PrepareAsync().GetAwaiter().GetResult();
                Console.WriteLine($"Isolated UI verification data: {paths.SettingsFile}");
                var builder = AppBuilder.Configure(() => new FastTorrentDownload.App(paths)).UsePlatformDetect();
                if (automated) builder.AfterSetup(_ => Dispatcher.UIThread.Post(async () => await VerifyAsync()));
                result = builder.StartWithClassicDesktopLifetime([]);
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
        return result;
    }

    private static async Task VerifyAsync()
    {
        await Task.Delay(700);
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        var main = (MainWindow)desktop.MainWindow!;
        var viewModel = (MainViewModel)main.DataContext!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            while (viewModel.Torrents.Count != 2) await Task.Delay(50, timeout.Token);
            Button Button(string name) => main.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, name));
            Require(!Button("Resume").IsEnabled && !Button("Remove").IsEnabled, "Actions must be disabled without a selection.");
            var row = viewModel.Torrents.Single(row => row.Name == "Selected files example");
            viewModel.SelectedTorrent = row;
            await Task.Delay(100);
            Require(Button("Resume").IsEnabled && Button("Remove").IsEnabled, "Selecting a row must enable its actions.");
            main.GetVisualDescendants().OfType<Expander>().First().IsExpanded = true;
            await Task.Delay(100);
            Require(row.Progress == 50 && row.DetailLabel.Contains("verified") && row.DiagnosticsText.Contains("Piece coverage"), "Selection metrics and diagnostics are missing.");

            Button("Settings").RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            await Task.Delay(150);
            var settings = main.OwnedWindows.OfType<SettingsWindow>().Single();
            Require(!settings.CanResize && settings.WindowState == WindowState.Normal,
                "Settings must disable native resizing and maximization.");
            Require(Math.Abs(settings.Width - main.Width) < 1 && Math.Abs(settings.Height - main.Height) < 1,
                "Settings must open at the same default size as the app.");
            Require(settings.FindControl<TextBox>("SeedRatioTextBox") is null && settings.FindControl<TextBox>("DownloadLimitTextBox") is not null, "Completion settings are inconsistent.");
            await VerifySettingsLayoutAsync(settings, 440);
            await VerifySettingsLayoutAsync(settings, 360);
            settings.Close(null);
            await Task.Delay(100);

            viewModel.SelectedTorrent = viewModel.Torrents.Single(row => row.Name == "Completed example");
            Button("Resume").RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            while (viewModel.Torrents.Count != 1 || !viewModel.HasNotification) await Task.Delay(50, timeout.Token);
            Require(Button("Open folder").IsVisible && Directory.Exists(viewModel.Notification?.Destination), "Completion must offer the saved folder.");
            Require(!Button("Remove").IsEnabled, "Removing the selected row must clear its selection.");

            Button("Activity").RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            await Task.Delay(150);
            var activity = main.OwnedWindows.OfType<ActivityWindow>().Single();
            Require(activity.Activity.Any(item => item.CanOpenFolder && item.Message.StartsWith("Completed")), "Activity must keep a completion and its folder.");
            activity.Close();
            await Task.Delay(100);
            viewModel.ExpireNotification(DateTimeOffset.UtcNow.AddMinutes(1));
            Require(!Button("Open folder").IsVisible && !viewModel.HasNotification, "The completion notification must expire.");

            main.Width = 880;
            main.Height = 600;
            viewModel.SetTheme("Light");
            await Task.Delay(200);
            Require(main.ActualThemeVariant == ThemeVariant.Light, "The light theme did not apply.");
            var actions = Button("Remove");
            var point = actions.TranslatePoint(default, main);
            Require(point is { } location && location.X >= 0 && location.X + actions.Bounds.Width <= main.Bounds.Width, "The toolbar overflows at the minimum desktop size.");
            Console.WriteLine("PASS native UI: selection/actions, diagnostics, settings, automatic completion/folder notification, activity, expiry, light/dark and minimum desktop size");
            main.RequestExit();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            desktop.Shutdown(1);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task VerifySettingsLayoutAsync(SettingsWindow settings, double height)
    {
        settings.SizeToContent = SizeToContent.Manual;
        settings.Width = 520;
        settings.Height = height;
        await Task.Delay(150);
        var scroll = ((Grid)settings.Content!).Children.OfType<ScrollViewer>().Single();
        Require(scroll.Extent.Height > scroll.Viewport.Height && scroll.Viewport.Height > 0,
            "Settings must have a bounded, scrollable viewport in a small window.");
        scroll.ScrollToEnd();
        await Task.Delay(100);
        var theme = settings.FindControl<ComboBox>("ThemeComboBox")!;
        var themePoint = theme.TranslatePoint(default, scroll);
        Require(scroll.Offset.Y > 0 && themePoint is { } location && location.Y >= 0 &&
            location.Y + theme.Bounds.Height <= scroll.Bounds.Height,
            "Scrolling Settings must reveal the final appearance control.");
        foreach (var name in new[] { "DownloadLimitTextBox", "UploadLimitTextBox" })
        {
            var control = settings.FindControl<TextBox>(name)!;
            var point = control.TranslatePoint(default, scroll);
            Require(point is { } position && position.X >= 0 &&
                position.X + control.Bounds.Width <= scroll.Viewport.Width,
                "Bandwidth fields must fit at the minimum Settings width.");
        }

        var save = settings.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Save settings"));
        settings.FindControl<TextBox>("ListenPortTextBox")!.Text = "0";
        save.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        await Task.Delay(100);
        var error = settings.FindControl<TextBlock>("ValidationErrorTextBlock")!;
        Require(error.IsVisible && !string.IsNullOrEmpty(error.Text), "Invalid settings must show validation feedback.");
        foreach (var control in new Control[] { error, save, settings.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Cancel")) })
        {
            var point = control.TranslatePoint(default, settings);
            Require(point is { } position && position.Y >= 0 &&
                position.Y + control.Bounds.Height <= settings.ClientSize.Height,
                "Settings validation and footer actions must remain visible outside the scroll area.");
        }
        Console.WriteLine($"PASS Settings scroll, final control, bandwidth fields and fixed validation/footer at 520x{height}");
    }

    private static async Task<AppPaths> PrepareAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "fast-torrent-ui-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        paths.EnsureDirectories();
        using var port = new TcpListener(IPAddress.Loopback, 0);
        port.Start();
        var listenPort = ((IPEndPoint)port.LocalEndpoint).Port;
        port.Stop();
        await new SettingsStore(paths).SaveAsync(new AppSettings
        {
            DefaultDownloadFolder = root, ListenPort = listenPort, ListenAddress = "127.0.0.1",
            EnableDht = false, EnablePeerExchange = false, EnablePortForwarding = false,
            EnableLocalPeerDiscovery = false, Theme = "Dark", ConfirmCloseBeforeExit = false
        });
        using var engine = new ClientEngine(new EngineSettingsBuilder
        {
            CacheDirectory = paths.CacheDirectory, DhtEndPoint = null, AllowLocalPeerDiscovery = false,
            AllowPortForwarding = false, ListenEndPoints = new Dictionary<string, IPEndPoint>()
        }.ToSettings());
        var service = new TorrentEngineService(paths);
        typeof(TorrentEngineService).GetField("_engine", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, engine);
        var data = new byte[16_384];
        Array.Fill(data, (byte)7);
        foreach (var name in new[] { "Selected files example", "Completed example" })
        {
            var encoded = new BEncodedDictionary
            {
                ["info"] = new BEncodedDictionary
                {
                    ["name"] = new BEncodedString(name), ["piece length"] = new BEncodedNumber(data.Length),
                    ["pieces"] = new BEncodedString(SHA1.HashData(data).Concat(SHA1.HashData(data)).ToArray()),
                    ["files"] = new BEncodedList
                    {
                        new BEncodedDictionary { ["length"] = new BEncodedNumber(data.Length), ["path"] = new BEncodedList { new BEncodedString("first.bin") } },
                        new BEncodedDictionary { ["length"] = new BEncodedNumber(data.Length), ["path"] = new BEncodedList { new BEncodedString("second.bin") } }
                    }
                }
            };
            var source = Path.Combine(root, name + ".torrent");
            await File.WriteAllBytesAsync(source, encoded.Encode());
            var preview = await service.PrepareAddAsync(source, root);
            await service.CommitAddAsync(preview, ["first.bin", "second.bin"], startImmediately: false);
            var manager = engine.Torrents.Single(manager => manager.Name == name);
            Directory.CreateDirectory(Path.GetDirectoryName(manager.Files[0].FullPath)!);
            await File.WriteAllBytesAsync(manager.Files[0].FullPath, data);
            if (name == "Completed example") await File.WriteAllBytesAsync(manager.Files[1].FullPath, data);
            await manager.HashCheckAsync(autoStart: false);
        }
        await engine.SaveStateAsync(paths.EngineStateFile);
        // Preserve the pause intents while writing the final fixture fast-resume state.
        var state = BEncodedValue.Decode<BEncodedDictionary>(await File.ReadAllBytesAsync(paths.EngineStateFile));
        state["PausedInfoHashes"] = new BEncodedList(service.PausedInfoHashes.Select(hash => (BEncodedValue)new BEncodedString(hash)));
        await File.WriteAllBytesAsync(paths.EngineStateFile, state.Encode());
        return paths;
    }
}
