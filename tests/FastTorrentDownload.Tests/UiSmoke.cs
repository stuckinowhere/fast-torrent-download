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
                var builder = AppBuilder.Configure(() => new FastTorrentDownload.App(paths, PipeName)).UsePlatformDetect();
                if (automated) builder.AfterSetup(_ => Dispatcher.UIThread.Post(async () => await VerifyAsync()));
                if (ShotsDirectory is { } shots) builder.AfterSetup(_ => Dispatcher.UIThread.Post(async () => await CaptureAsync(shots)));
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

    /// <summary>Set by <c>--ui-shots &lt;dir&gt;</c>: render the fixture UI to PNG files for design review.</summary>
    public static string? ShotsDirectory { get; set; }

    private static async Task CaptureAsync(string directory)
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        try
        {
            Directory.CreateDirectory(directory);
            await Task.Delay(700);
            var main = (MainWindow)desktop.MainWindow!;
            var viewModel = (MainViewModel)main.DataContext!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (viewModel.Torrents.Count != 2) await Task.Delay(50, timeout.Token);
            foreach (var update in main.OwnedWindows.OfType<UpdateAvailableWindow>().ToArray()) update.Close();
            async Task ShotAsync(string name)
            {
                await Task.Delay(350);
                var size = new PixelSize((int)main.ClientSize.Width, (int)main.ClientSize.Height);
                using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(size, new Vector(96, 96));
                bitmap.Render(main);
                bitmap.Save(Path.Combine(directory, name + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }

            await ShotAsync("queue-dark");
            viewModel.SelectedTorrent = viewModel.Torrents[0];
            viewModel.Torrents[0].IsDetailsOpen = true;
            await ShotAsync("queue-dark-expanded");
            viewModel.Torrents[0].IsDetailsOpen = false;
            viewModel.SetTheme("Light");
            await ShotAsync("queue-light");
            main.Width = 880;
            main.Height = 600;
            await ShotAsync("queue-light-min");
            main.Width = 1180;
            main.Height = 760;
            main.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Settings"))
                .RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            await ShotAsync("settings-light");
            viewModel.SetTheme("Dark");
            await ShotAsync("settings-dark");
            Console.WriteLine($"Saved UI captures to {directory}");
            main.RequestExit();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            desktop.Shutdown(1);
        }
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
            Require(viewModel.TrayToolTipText.Contains("2 torrents"), "Tray tooltip must reflect the active queue summary.");
            Button Button(string name) => main.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, name));
            Button RowButton(TorrentRowViewModel target, string name) => main.GetVisualDescendants().OfType<Button>()
                .Single(button => Equals(button.Content, name) && ReferenceEquals(button.DataContext, target));
            var row = viewModel.Torrents.Single(row => row.Name == "Selected files example");
            await Task.Delay(100);
            // Per-row actions need no selection step: a stopped row offers Resume and hides Pause.
            Require(RowButton(row, "Resume").IsVisible && RowButton(row, "Resume").IsEnabled && !RowButton(row, "Pause").IsVisible, "A stopped row must offer Resume directly.");
            Require(RowButton(row, "Files…").IsEnabled && RowButton(row, "Move…").IsEnabled && RowButton(row, "Remove").IsEnabled, "Row actions must be enabled while idle.");
            viewModel.Torrents[0].IsDetailsOpen = true;
            await Task.Delay(100);
            Require(row.Progress == 50 && row.DetailLabel.Contains("verified") && row.DiagnosticsText.Contains("Piece coverage"), "Selection metrics and diagnostics are missing.");

            Button("Settings").RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            await Task.Delay(150);
            foreach (var update in main.OwnedWindows.OfType<UpdateAvailableWindow>().ToArray()) update.Close();
            Require(viewModel.IsSettingsOpen && !viewModel.IsQueueOpen && main.OwnedWindows.Count == 0,
                $"Settings must open inside MainWindow: settings={viewModel.IsSettingsOpen}, queue={viewModel.IsQueueOpen}, owned={string.Join(',', main.OwnedWindows.Select(window => window.GetType().Name))}.");
            Require(main.FindControl<TextBox>("SeedRatioTextBox") is null && main.FindControl<TextBox>("DownloadLimitTextBox") is not null, "Completion settings are inconsistent.");
            Require(main.FindControl<CheckBox>("PublicCachesCheckBox")!.IsChecked == false && main.FindControl<TextBox>("MaxActiveTextBox")!.Text == "0",
                "Privacy and queue settings must default to off / unlimited.");
            await VerifySettingsLayoutAsync(main, 600);
            main.Width = 1180;
            main.Height = 760;
            await Task.Delay(100);
            Button("Cancel").RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            await Task.Delay(100);
            Require(!viewModel.IsSettingsOpen && viewModel.IsQueueOpen, "Cancel must return from Settings to the torrent queue page.");
            await VerifyAddDialogsAsync(main);

            // Collapse the long diagnostics panel so the virtualized second row is realized again.
            viewModel.Torrents[0].IsDetailsOpen = false;
            var completed = viewModel.Torrents.Single(row => row.Name == "Completed example");
            main.GetVisualDescendants().OfType<ListBox>().First().ScrollIntoView(completed);
            await Task.Delay(150);
            RowButton(completed, "Resume").RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            while (viewModel.Torrents.Count != 1 || !viewModel.HasNotification) await Task.Delay(50, timeout.Token);
            Require(Button("Open folder").IsVisible && Directory.Exists(viewModel.Notification?.Destination), "Completion must offer the saved folder.");
            Require(viewModel.SelectedTorrent is null, "Removing the selected row must clear its selection.");

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
            var actions = RowButton(row, "Remove");
            var point = actions.TranslatePoint(default, main);
            Require(point is { } location && location.X >= 0 && location.X + actions.Bounds.Width <= main.Bounds.Width, "The row actions overflow at the minimum desktop size.");
            Console.WriteLine("PASS native UI: selection/actions, diagnostics, in-app settings page, automatic completion/folder notification, activity, expiry, light/dark and minimum desktop size");
            main.Width = 1180;
            main.Height = 760;
            await VerifyEndToEndAsync(main, viewModel, _fixtureRoot!, timeout.Token);
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

    private static string? _fixtureRoot;
    private static readonly string PipeName = "WASD.FastTorrentDownload.test-" + Guid.NewGuid().ToString("N");

    // Exercises the real App (engine, pipe listener, dialogs) with the isolated fixture:
    // the paths a user actually clicks, not the view-model shortcuts.
    private static async Task VerifyEndToEndAsync(MainWindow main, MainViewModel viewModel, string root, CancellationToken token)
    {
        var click = new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent);
        // The queue list virtualizes: a row that is off-screen or was just added has no buttons yet.
        Button RowButton(TorrentRowViewModel target, string name)
        {
            Button? Find() => main.GetVisualDescendants().OfType<Button>()
                .SingleOrDefault(button => Equals(button.Content, name) && ReferenceEquals(button.DataContext, target));
            if (Find() is { } ready) return ready;
            var list = main.GetVisualDescendants().OfType<ListBox>().First();
            list.ScrollIntoView(target);
            list.UpdateLayout();
            return Find() ?? throw new InvalidOperationException(
                $"Row button '{name}' for '{target.Name}' is not realized. Rows: {string.Join(", ", viewModel.Torrents.Select(row => $"{row.Name}={row.State}"))}; window {main.ClientSize}; list {list.Bounds}.");
        }
        async Task UntilAsync(Func<bool> condition, string message)
        {
            using var step = CancellationTokenSource.CreateLinkedTokenSource(token);
            step.CancelAfter(TimeSpan.FromSeconds(20));
            try { while (!condition()) await Task.Delay(50, step.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new InvalidOperationException(message); }
        }
        async Task<SelectTorrentFilesWindow> PickerAsync()
        {
            SelectTorrentFilesWindow? picker = null;
            await UntilAsync(() => (picker = main.OwnedWindows.OfType<SelectTorrentFilesWindow>().FirstOrDefault()) is not null, "The file picker did not open.");
            await UntilAsync(() => picker!.FindControl<Button>("AddSelectedButton")!.IsEnabled, "The file picker never finished loading.");
            return picker!;
        }

        // 1. A second launch forwards a .torrent over the real named pipe; the running window adds it.
        var forwarded = Path.Combine(root, "Forwarded example.torrent");
        Require(SingleInstanceChannel.TryForward([forwarded], PipeName), "Forwarding to the running instance failed.");
        var picker = await PickerAsync();
        picker.FindControl<Button>("AddSelectedButton")!.RaiseEvent(click);
        await UntilAsync(() => viewModel.Torrents.Count == 2, "The forwarded torrent was not added.");
        var added = viewModel.Torrents.Single(row => row.Name == "Forwarded example");
        await UntilAsync(() => added.State is "Downloading" or "Hashing", $"The forwarded torrent did not start: {added.State}.");

        // 2. Files… on a running torrent drops a file and persists the change.
        RowButton(added, "Files…").RaiseEvent(click);
        picker = await PickerAsync();
        Require(picker.Title == "Change downloaded files" && picker.Files.Count == 2 && picker.Files.All(file => file.IsSelected), "Edit mode must preload the current selection.");
        picker.Files.Single(file => file.Path == "second.bin").IsSelected = false;
        picker.FindControl<Button>("AddSelectedButton")!.RaiseEvent(click);
        // The dialog closes before the row reports "Updating…", so poll the engine rather than the busy flag.
        var applied = false;
        await UntilAsync(() =>
        {
            var files = viewModel.GetFilesAsync(added.Id).GetAwaiter().GetResult();
            applied = !files.Files.Single(file => file.Path == "second.bin").Selected && files.Files.Single(file => file.Path == "first.bin").Selected;
            return applied || viewModel.StatusMessage.StartsWith("Could not", StringComparison.Ordinal);
        }, "The file update did not finish.");
        Require(applied, $"The new file selection was not applied: {viewModel.StatusMessage}");
        await UntilAsync(() => !added.IsBusy, "The row stayed busy after the file update.");

        // 3. Maximum active downloads = 1 through the Settings page: the next Resume waits as Queued.
        main.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Settings")).RaiseEvent(click);
        await UntilAsync(() => viewModel.IsSettingsOpen, "Settings did not open.");
        main.FindControl<TextBox>("MaxActiveTextBox")!.Text = "1";
        main.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Save settings")).RaiseEvent(click);
        await UntilAsync(() => !viewModel.IsSettingsOpen && viewModel.Settings.MaximumActiveDownloads == 1, "The active-download limit was not saved.");
        var partial = viewModel.Torrents.Single(row => row.Name == "Selected files example");
        RowButton(partial, "Resume").RaiseEvent(click);
        await UntilAsync(() => partial.State == "Queued", $"The second torrent should wait as Queued: {partial.State}.");
        Require(RowButton(partial, "Pause").IsVisible && !RowButton(partial, "Resume").IsVisible, "A queued row must offer Pause.");
        RowButton(added, "Pause").RaiseEvent(click);
        await UntilAsync(() => added.State == "Stopped" && !added.IsBusy, "Pause did not stop the active torrent.");
        await UntilAsync(() => partial.State is "Downloading" or "Hashing", $"The queued torrent was not promoted: {partial.State}.");

        // 4. Move… relocates real payload data of a running torrent and keeps it running.
        var oldFile = Directory.EnumerateFiles(root, "first.bin", SearchOption.AllDirectories).Single(file => file.Contains("Selected files example"));
        var moved = Path.Combine(root, "moved");
        await viewModel.MoveTorrentAsync(partial.Id, moved);
        await UntilAsync(() => partial.Destination == moved && !partial.IsBusy, "Move did not update the row destination.");
        var newFile = Directory.EnumerateFiles(moved, "first.bin", SearchOption.AllDirectories).Single();
        Require(!File.Exists(oldFile) && new FileInfo(newFile).Length == 16_384, "Move did not relocate the payload.");
        await UntilAsync(() => partial.State is "Downloading" or "Hashing", $"The moved torrent did not resume: {partial.State}.");

        // 4b. Trackers… adds a tracker through the dialog; Delete files… asks first, then removes row and data.
        RowButton(partial, "Trackers…").RaiseEvent(click);
        TrackersWindow? trackers = null;
        await UntilAsync(() => (trackers = main.OwnedWindows.OfType<TrackersWindow>().FirstOrDefault()) is not null, "The trackers dialog did not open.");
        trackers!.FindControl<TextBox>("NewTrackersTextBox")!.Text = "udp://127.0.0.1:9/announce\nnot-a-tracker";
        trackers!.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Add trackers")).RaiseEvent(click);
        await UntilAsync(() => viewModel.StatusMessage.StartsWith("Added 1 tracker to Selected files example. Ignored 1 invalid entry", StringComparison.Ordinal),
            $"Adding a tracker did not report its result: {viewModel.StatusMessage}");
        await UntilAsync(() => partial.Trackers.Any(tracker => tracker.Address == "udp://127.0.0.1:9"), "The added tracker is not listed.");

        RowButton(partial, "Delete files…").RaiseEvent(click);
        DeleteFilesWindow? confirm = null;
        await UntilAsync(() => (confirm = main.OwnedWindows.OfType<DeleteFilesWindow>().FirstOrDefault()) is not null, "The delete confirmation did not open.");
        Require(File.Exists(newFile), "Files must not be deleted before the confirmation.");
        confirm!.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Cancel")).RaiseEvent(click);
        await UntilAsync(() => main.OwnedWindows.Count == 0, "Cancel did not close the delete confirmation.");
        Require(File.Exists(newFile) && viewModel.Torrents.Contains(partial), "Cancel must keep the torrent and its files.");
        RowButton(partial, "Delete files…").RaiseEvent(click);
        await UntilAsync(() => (confirm = main.OwnedWindows.OfType<DeleteFilesWindow>().FirstOrDefault()) is not null, "The delete confirmation did not reopen.");
        confirm!.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Delete files")).RaiseEvent(click);
        await UntilAsync(() => !viewModel.Torrents.Contains(partial), "Delete files did not remove the torrent.");
        Require(!File.Exists(newFile) && Directory.Exists(moved), "Delete files must remove the payload and keep the chosen folder.");

        // 5. Closing the window keeps the app in the tray; the tray command brings it back.
        main.Close();
        await UntilAsync(() => !main.IsVisible, "Close must hide the window when CloseToTray is on.");
        Require(viewModel.StatusMessage.Contains("background"), "Hiding must explain that downloads keep running.");
        main.Show();
        await UntilAsync(() => main.IsVisible, "Show must bring the window back.");

        // 6. Start-with-Windows writes and removes the per-user Run entry; the prior value is restored.
        using var run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        var before = run?.GetValue("FastTorrentDownload") as string;
        try
        {
            StartupRegistration.Apply(true);
            var value = run?.GetValue("FastTorrentDownload") as string;
            Require(value is not null && value.Contains(Environment.ProcessPath!) && value.EndsWith(StartupRegistration.MinimizedArgument), "The Run entry was not written.");
            StartupRegistration.Apply(false);
            Require(run?.GetValue("FastTorrentDownload") is null, "The Run entry was not removed.");
        }
        finally
        {
            // Leave the Run key exactly as it was, even when an assertion above threw after Apply(true).
            using var writable = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (before is not null) writable?.SetValue("FastTorrentDownload", before);
            else writable?.DeleteValue("FastTorrentDownload", throwOnMissingValue: false);
        }

        Console.WriteLine("PASS end-to-end: pipe forwarding adds a torrent, Files… edits a running torrent, active-download queue promotes, Move relocates data, Trackers… adds one, Delete files… confirms then removes data, close-to-tray and Run entry round-trip");
    }

    private static async Task VerifySettingsLayoutAsync(MainWindow main, double height)
    {
        main.Width = 880;
        main.Height = height;
        await Task.Delay(150);
        var scroll = main.FindControl<ScrollViewer>("SettingsScrollViewer")!;
        Require(scroll.Viewport.Height > 0, "Settings page must have a positive viewport inside MainWindow.");
        scroll.ScrollToEnd();
        await Task.Delay(100);
        var theme = main.FindControl<ComboBox>("ThemeComboBox")!;
        var themePoint = theme.TranslatePoint(default, scroll);
        Require(themePoint is { } location && location.Y >= 0 &&
            location.Y + theme.Bounds.Height <= scroll.Bounds.Height + 1,
            "Scrolling the Settings page must reveal the final appearance control.");
        foreach (var name in new[] { "DownloadLimitTextBox", "UploadLimitTextBox" })
        {
            var control = main.FindControl<TextBox>(name)!;
            var point = control.TranslatePoint(default, scroll);
            Require(point is { } position && position.X >= 0 &&
                position.X + control.Bounds.Width <= scroll.Viewport.Width,
                "Bandwidth fields must fit inside the Settings page width.");
        }

        var save = main.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Save settings"));
        main.FindControl<TextBox>("ListenPortTextBox")!.Text = "0";
        save.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        await Task.Delay(100);
        var error = main.FindControl<TextBlock>("ValidationErrorTextBlock")!;
        Require(error.IsVisible && !string.IsNullOrEmpty(error.Text), "Invalid settings must show validation feedback.");
        foreach (var control in new Control[] { error, save, main.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Cancel")) })
        {
            var point = control.TranslatePoint(default, main);
            Require(point is { } position && position.Y >= 0 &&
                position.Y + control.Bounds.Height <= main.ClientSize.Height,
                "Settings validation and footer actions must remain visible outside the scroll area.");
        }
        Console.WriteLine($"PASS in-app Settings page scroll, final control, bandwidth fields and fixed validation/footer at 880x{height}");
    }

    private static async Task VerifyAddDialogsAsync(MainWindow main)
    {
        var destination = @"D:\Downloads\" + new string('d', 200);
        var source = "magnet:?xt=urn:btih:" + new string('a', 40) + "&dn=" + new string('n', 300);
        var add = new AddTorrentWindow(destination);
        var requestTask = add.ShowDialog<AddTorrentRequest?>(main);
        await Task.Delay(100);
        add.FindControl<TextBox>("SourceTextBox")!.Text = source;
        foreach (var width in new[] { 650, 520 })
        {
            add.Width = width;
            await Task.Delay(100);
            Require(Math.Abs(add.ClientSize.Width - width) < 2, $"Add dialog did not reach the requested width: {add.ClientSize}.");
            foreach (var name in new[] { "SourceTextBox", "DestinationTextBox", "StartImmediatelyCheckBox" })
                RequireInside(add.FindControl<Control>(name)!, add, name);
            foreach (var button in add.GetVisualDescendants().OfType<Button>().Where(button => button.Content is string))
                RequireInside(button, add, $"Add torrent: {button.Content}");
        }
        add.FindControl<CheckBox>("StartImmediatelyCheckBox")!.IsChecked = false;
        add.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Add torrent"))
            .RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        var request = await requestTask;
        Require(request is { StartImmediately: false } && request.Source == source && request.Destination == destination,
            "Adding must preserve the source, destination and start choice.");

        var picker = new SelectTorrentFilesWindow(new string('t', 200));
        var selectionTask = picker.ShowDialog<string[]?>(main);
        await Task.Delay(100);
        RequireInside(picker.FindControl<StackPanel>("LoadingPanel")!, picker, "File-list loading state");
        var files = Enumerable.Range(0, 100).Select(index => new TorrentFilePreview($"folder/{new string('f', 180)}-{index}.bin", 1_234_567_890)).ToArray();
        picker.ShowPreview(new TorrentAddPreview("layout-check", new string('t', 200), files));
        picker.Width = 560;
        picker.Height = 420;
        await Task.Delay(150);
        Require(Math.Abs(picker.ClientSize.Width - 560) < 2 && Math.Abs(picker.ClientSize.Height - 420) < 2,
            $"File selection did not reach its minimum size: {picker.ClientSize}.");
        foreach (var button in picker.GetVisualDescendants().OfType<Button>().Where(button => button.Content is string))
            RequireInside(button, picker, $"File selection: {button.Content}");
        picker.FindControl<Button>("SelectNoneButton")!.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Require(picker.Files.All(file => !file.IsSelected), "Select none must clear the selection.");
        Require(picker.FindControl<TextBlock>("SelectionSummaryTextBlock")!.Text!.StartsWith("0 / 100 selected", StringComparison.Ordinal),
            "Selection summary must reflect 0 selected files.");
        picker.FindControl<Button>("AddSelectedButton")!.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        await Task.Delay(100);
        Require(!selectionTask.IsCompleted, "An empty selection must keep the dialog open.");
        RequireInside(picker.FindControl<TextBlock>("StatusTextBlock")!, picker, "File-selection validation");
        foreach (var button in picker.GetVisualDescendants().OfType<Button>().Where(button => button.Content is string))
            RequireInside(button, picker, $"File selection with validation: {button.Content}");
        picker.FindControl<Button>("SelectAllButton")!.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Require(picker.Files.All(file => file.IsSelected), "Select all must restore the selection.");
        Require(picker.FindControl<TextBlock>("SelectionSummaryTextBlock")!.Text!.StartsWith("100 / 100 selected", StringComparison.Ordinal),
            "Selection summary must reflect all selected files.");
        var list = picker.FindControl<ListBox>("FilesListBox")!;
        list.ScrollIntoView(picker.Files[^1]);
        await Task.Delay(150);
        var lastRow = list.GetVisualDescendants().OfType<CheckBox>().Single(box => ReferenceEquals(box.DataContext, picker.Files[^1]));
        RequireInside(lastRow, picker, "Last file after scrolling");
        var lastPoint = lastRow.TranslatePoint(default, list);
        Require(lastPoint is { } lastPosition && lastPosition.Y >= 0 && lastPosition.Y + lastRow.Bounds.Height <= list.Bounds.Height + 1,
            "Scrolling must reveal the entire final file row inside the list.");
        foreach (var text in lastRow.GetVisualDescendants().OfType<TextBlock>())
        {
            var point = text.TranslatePoint(default, list);
            Require(point is { } position && position.X >= 0 && position.X + text.Bounds.Width <= list.Bounds.Width + 1,
                "Long file names must leave their size and checkbox visible.");
        }
        foreach (var file in picker.Files.Skip(1)) file.IsSelected = false;
        picker.FindControl<Button>("AddSelectedButton")!.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Require((await selectionTask)!.SequenceEqual(new[] { files[0].Path }), "Only the selected file must be returned.");
        Console.WriteLine("PASS Add dialog at widths 650/520, file selection at 560x420, loading/validation, long names and 100-file scrolling");
    }

    private static void RequireInside(Control control, Window window, string label)
    {
        var point = control.TranslatePoint(default, window);
        Require(control.IsVisible && control.Bounds.Width > 0 && control.Bounds.Height > 0 &&
            point is { } position && position.X >= 0 && position.Y >= 0 &&
            position.X + control.Bounds.Width <= window.ClientSize.Width + 1 &&
            position.Y + control.Bounds.Height <= window.ClientSize.Height + 1,
            $"{label} is clipped or inaccessible at {window.ClientSize}.");
    }

    private static async Task<AppPaths> PrepareAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "fast-torrent-ui-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        paths.EnsureDirectories();
        _fixtureRoot = root;
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
        foreach (var name in new[] { "Selected files example", "Completed example", "Forwarded example" })
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
            if (name == "Forwarded example") continue; // Added later through the second-instance pipe.
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
        // Mark the fixture as a current-format queue so restore never seeds public trackers for it.
        state["ExtraTrackers"] = new BEncodedDictionary();
        await File.WriteAllBytesAsync(paths.EngineStateFile, state.Encode());
        return paths;
    }
}
