using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using FastTorrentDownload.Models;
using FastTorrentDownload.ViewModels;

namespace FastTorrentDownload.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Queue<string> _externalSources = new();
    private readonly bool _startHidden;
    private bool _engineReady;
    private bool _addInProgress;
    private bool _allowClose;
    private bool _shutdownStarted;
    private bool _exitRequested;
    private bool _skipExitConfirmation;

    public MainWindow(MainViewModel viewModel, bool startHidden = false)
    {
        _viewModel = viewModel;
        _startHidden = startHidden;
        DataContext = viewModel;
        InitializeComponent();
        Opened += OnOpened;
        Closing += OnClosing;
        _refreshTimer.Tick += OnRefreshTick;
    }

    private async void OnOpened(object? sender, EventArgs eventArgs)
    {
        try
        {
            if (_startHidden)
            {
                Hide();
            }

            await _viewModel.InitializeAsync();
            _engineReady = true;
            _refreshTimer.Start();
            await DrainExternalSourcesAsync();
            await ShowUpdateDialogIfAvailableAsync(quiet: true);
        }
        catch (Exception exception)
        {
            _viewModel.NotifyActionFailure($"Could not start the torrent engine: {exception.Message}");
        }
    }

    private async void OnRefreshTick(object? sender, EventArgs eventArgs) => await _viewModel.RefreshAsync();

    /// <summary>Sources handed over at launch; they are added once the engine is ready.</summary>
    public void QueueExternalSources(IEnumerable<string> sources)
    {
        foreach (var source in sources)
        {
            _externalSources.Enqueue(source);
        }
    }

    /// <summary>A magnet click or .torrent open forwarded by a second instance.</summary>
    public void OpenExternalSource(string source)
    {
        _externalSources.Enqueue(source);
        Show();
        Activate();
        _ = DrainExternalSourcesAsync();
    }

    private async Task DrainExternalSourcesAsync()
    {
        if (!_engineReady || _addInProgress)
        {
            return;
        }

        _addInProgress = true;
        try
        {
            while (_externalSources.TryDequeue(out var source))
            {
                _viewModel.IsSettingsOpen = false;
                await AddTorrentAsync(new AddTorrentRequest(source, null, StartImmediately: true));
            }
        }
        finally
        {
            _addInProgress = false;
        }
    }

    private async void Add_Click(object? sender, RoutedEventArgs eventArgs)
    {
        var request = await new AddTorrentWindow(_viewModel.Settings.DefaultDownloadFolder) { Icon = Icon }.ShowDialog<AddTorrentRequest?>(this);
        if (request is null)
        {
            return;
        }

        _viewModel.IsSettingsOpen = false;
        await AddTorrentAsync(request);
    }

    protected override async void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || _viewModel.IsSettingsOpen || e.Source is TextBox || e.Key != Key.V || !e.KeyModifiers.HasFlag(KeyModifiers.Control) || Clipboard is null)
        {
            return;
        }

        try
        {
            var text = (await Clipboard.TryGetTextAsync())?.Trim();
            if (string.IsNullOrWhiteSpace(text) || !IsTorrentSource(text))
            {
                return;
            }

            e.Handled = true;
            await AddTorrentAsync(new AddTorrentRequest(text, null, StartImmediately: true));
        }
        catch (Exception exception)
        {
            // An async void handler must never let a clipboard failure take the app down.
            Services.AppLogger.LogException("Clipboard paste failed", exception);
            _viewModel.NotifyActionFailure($"Could not read the clipboard: {exception.Message}");
        }
    }

    private async void Drop_Handle(object? sender, DragEventArgs e)
    {
        try
        {
            if (e.DataTransfer is not IAsyncDataTransfer transfer)
            {
                return;
            }

            var files = await transfer.TryGetFilesAsync();
            var path = files?.Select(file => file.TryGetLocalPath()).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
            var source = (string.IsNullOrWhiteSpace(path) ? await transfer.TryGetTextAsync() : path) ?? string.Empty;
            var trimmed = source.Trim();
            if (!IsTorrentSource(trimmed))
            {
                _viewModel.StatusMessage = "Drop a .torrent file here, or add a magnet link.";
                return;
            }

            _viewModel.IsSettingsOpen = false;
            await AddTorrentAsync(new AddTorrentRequest(trimmed, null, StartImmediately: true));
        }
        catch (Exception exception)
        {
            Services.AppLogger.LogException("Drop failed", exception);
            _viewModel.NotifyActionFailure($"Could not read the dropped item: {exception.Message}");
        }
    }

    private static bool IsTorrentSource(string text) =>
        text.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Path.GetExtension(text), ".torrent", StringComparison.OrdinalIgnoreCase);

    private async Task AddTorrentAsync(AddTorrentRequest request)
    {
        using var fetch = new CancellationTokenSource();
        var picker = new SelectTorrentFilesWindow(PendingTorrentName(request.Source)) { Icon = Icon };
        picker.Closed += (_, _) => fetch.Cancel();
        var previewTask = _viewModel.PrepareAddAsync(
            request.Source,
            request.Destination,
            fetch.Token,
            (status, detail) => Dispatcher.UIThread.Post(() => picker.UpdateLoadingStatus(status, detail)));
        var dialogTask = picker.ShowDialog<string[]?>(this);

        TorrentAddPreview? preview = null;
        string? loadError = null;
        try
        {
            preview = await previewTask;
        }
        catch (Exception exception)
        {
            if (fetch.IsCancellationRequested || exception is OperationCanceledException)
            {
                _viewModel.StatusMessage = "Add cancelled.";
                return;
            }

            loadError = exception.Message;
            picker.FailAndClose();
        }

        if (preview is not null)
        {
            picker.ShowPreview(preview);
        }

        var selectedFiles = await dialogTask;
        if (selectedFiles is null || preview is null)
        {
            if (preview is not null)
            {
                await _viewModel.CancelAddAsync(preview.Id);
            }

            if (loadError is null)
            {
                _viewModel.StatusMessage = "Add cancelled.";
            }
            else
            {
                _viewModel.NotifyActionFailure($"Could not add torrent: {loadError}");
            }

            return;
        }

        try
        {
            await _viewModel.AddAsync(preview, selectedFiles, request.StartImmediately);
        }
        catch (Exception exception)
        {
            await _viewModel.CancelAddAsync(preview.Id);
            _viewModel.NotifyActionFailure($"Could not add torrent: {exception.Message}");
        }
    }

    public static string PendingTorrentName(string source)
    {
        var trimmed = source.Trim();
        if (trimmed.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            if (MonoTorrent.MagnetLink.TryParse(trimmed, out var magnet) && !string.IsNullOrWhiteSpace(magnet.Name))
            {
                var clean = Services.TorrentEngineService.SanitizeDisplayName(magnet.Name);
                if (!string.IsNullOrWhiteSpace(clean))
                {
                    return clean;
                }
            }

            return "Magnet link";
        }

        var fileName = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(fileName) ? "Torrent file" : fileName;
    }

    private static TorrentRowViewModel? RowOf(object? sender) => (sender as Control)?.DataContext as TorrentRowViewModel;

    private async void RowResume_Click(object? sender, RoutedEventArgs eventArgs) => await RunRowActionAsync(sender, _viewModel.StartTorrentAsync);
    private async void RowPause_Click(object? sender, RoutedEventArgs eventArgs) => await RunRowActionAsync(sender, _viewModel.PauseTorrentAsync);
    private async void RowRecheck_Click(object? sender, RoutedEventArgs eventArgs) => await RunRowActionAsync(sender, _viewModel.RecheckTorrentAsync);
    private async void RowRemove_Click(object? sender, RoutedEventArgs eventArgs) => await RunRowActionAsync(sender, _viewModel.RemoveTorrentAsync);
    private void RowFolder_Click(object? sender, RoutedEventArgs eventArgs) => _viewModel.OpenTorrentFolder(RowOf(sender));

    private async void RowFiles_Click(object? sender, RoutedEventArgs eventArgs)
    {
        if (RowOf(sender) is not { } row)
        {
            return;
        }

        _viewModel.SelectedTorrent = row;
        await RunActionAsync(async () =>
        {
            var files = await _viewModel.GetFilesAsync(row.Id);
            var picker = new SelectTorrentFilesWindow(files.Name, editing: true) { Icon = Icon };
            var dialogTask = picker.ShowDialog<string[]?>(this);
            picker.ShowPreview(files);
            var selected = await dialogTask;
            if (selected is not null)
            {
                await _viewModel.ChangeFilesAsync(row.Id, selected);
            }
        });
    }

    private async void RowMove_Click(object? sender, RoutedEventArgs eventArgs)
    {
        if (RowOf(sender) is not { } row)
        {
            return;
        }

        _viewModel.SelectedTorrent = row;
        await RunActionAsync(async () =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = $"Move “{row.Name}” to…",
                AllowMultiple = false
            });
            if (folders.Count == 0)
            {
                return;
            }

            var destination = folders[0].TryGetLocalPath() ?? folders[0].Path.LocalPath;
            await _viewModel.MoveTorrentAsync(row.Id, destination);
        });
    }

    private async Task RunRowActionAsync(object? sender, Func<string, Task> action)
    {
        if (RowOf(sender) is not { } row)
        {
            return;
        }

        _viewModel.SelectedTorrent = row;
        await RunActionAsync(() => action(row.Id));
    }

    private void Folder_Click(object? sender, RoutedEventArgs eventArgs) => _viewModel.OpenTorrentFolder(null);
    private void NotificationFolder_Click(object? sender, RoutedEventArgs eventArgs) => _viewModel.OpenNotificationFolder();
    private async void PortGuide_Click(object? sender, RoutedEventArgs eventArgs)
    {
        var info = Services.NetworkBinding.DetectLocalNetworkInfo(_viewModel.Settings.ListenPort);
        await new PortForwardingGuideWindow(info) { Icon = Icon }.ShowDialog(this);
    }

    private void DismissPortWarning_Click(object? sender, RoutedEventArgs eventArgs) => _viewModel.DismissPortWarning();

    private async void Activity_Click(object? sender, RoutedEventArgs eventArgs) =>
        await new ActivityWindow(_viewModel.Activity) { Icon = Icon }.ShowDialog(this);

    private void ThemeToggle_Click(object? sender, RoutedEventArgs eventArgs)
    {
        var next = Avalonia.Application.Current?.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark ? "Light" : "Dark";
        _viewModel.SetTheme(next);
        if (_viewModel.IsSettingsOpen)
        {
            ThemeComboBox.SelectedIndex = next == "Light" ? 1 : 2;
        }
    }

    private async void Update_Click(object? sender, RoutedEventArgs eventArgs) =>
        await ShowUpdateDialogIfAvailableAsync(quiet: false);

    private async Task ShowUpdateDialogIfAvailableAsync(bool quiet)
    {
        var update = await _viewModel.CheckForUpdatesAsync(quiet);
        if (update is null)
        {
            if (!quiet)
            {
                await new UpToDateWindow(_viewModel.AppVersionLabel) { Icon = Icon }.ShowDialog(this);
            }

            return;
        }

        if (quiet && !IsVisible)
        {
            // A tray-only session must not pop a modal; the next manual check will offer it.
            return;
        }

        var installed = await new UpdateAvailableWindow(update, new Services.AppUpdateService()) { Icon = Icon }.ShowDialog<bool>(this);
        if (installed)
        {
            ExitForUpdate();
        }
    }

    private void Settings_Click(object? sender, RoutedEventArgs eventArgs)
    {
        if (_viewModel.IsSettingsOpen)
        {
            _viewModel.IsSettingsOpen = false;
            return;
        }

        LoadSettingsEditor(_viewModel.Settings);
        _viewModel.IsSettingsOpen = true;
    }

    private void LoadSettingsEditor(AppSettings settings)
    {
        var copy = settings.Copy();
        DefaultDownloadFolderTextBox.Text = copy.DefaultDownloadFolder;
        MaxActiveTextBox.Text = copy.MaximumActiveDownloads.ToString(CultureInfo.InvariantCulture);
        ListenPortTextBox.Text = copy.ListenPort.ToString(CultureInfo.InvariantCulture);
        ListenAddressTextBox.Text = copy.ListenAddress;
        DhtCheckBox.IsChecked = copy.EnableDht;
        PexCheckBox.IsChecked = copy.EnablePeerExchange;
        LsdCheckBox.IsChecked = copy.EnableLocalPeerDiscovery;
        PortForwardingCheckBox.IsChecked = copy.EnablePortForwarding;
        EncryptionCheckBox.IsChecked = copy.RequireEncryptedPeerConnections;
        PublicCachesCheckBox.IsChecked = copy.UsePublicMetadataCaches;
        DownloadLimitTextBox.Text = copy.MaximumDownloadKiBPerSecond.ToString(CultureInfo.InvariantCulture);
        UploadLimitTextBox.Text = copy.MaximumUploadKiBPerSecond.ToString(CultureInfo.InvariantCulture);
        CloseToTrayCheckBox.IsChecked = copy.CloseToTray;
        StartWithWindowsCheckBox.IsChecked = copy.StartWithWindows;
        ThemeComboBox.SelectedIndex = copy.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 };
        ValidationErrorTextBlock.Text = string.Empty;
        ValidationErrorTextBlock.IsVisible = false;
        SettingsScrollViewer.Offset = default;
    }

    private async void ChooseFolder_Click(object? sender, RoutedEventArgs eventArgs)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose default download folder",
            AllowMultiple = false
        });

        if (folders.Count > 0)
        {
            DefaultDownloadFolderTextBox.Text = folders[0].TryGetLocalPath() ?? folders[0].Path.LocalPath;
        }
    }

    private void CancelSettings_Click(object? sender, RoutedEventArgs eventArgs)
    {
        ValidationErrorTextBlock.Text = string.Empty;
        ValidationErrorTextBlock.IsVisible = false;
        _viewModel.IsSettingsOpen = false;
    }

    private async void SaveSettings_Click(object? sender, RoutedEventArgs eventArgs)
    {
        if (!int.TryParse(ListenPortTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var listenPort) ||
            listenPort is < 1024 or > 65535)
        {
            ShowSettingsValidationError("Port must be between 1024 and 65535.");
            return;
        }

        try
        {
            Services.NetworkBinding.ResolveListenAddress(ListenAddressTextBox.Text);
        }
        catch (ArgumentException exception)
        {
            ShowSettingsValidationError(exception.Message);
            return;
        }

        if (!int.TryParse(DownloadLimitTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var downloadLimit) || downloadLimit < 0 ||
            !int.TryParse(UploadLimitTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var uploadLimit) || uploadLimit < 0)
        {
            ShowSettingsValidationError("Use non-negative numeric limits.");
            return;
        }

        if (!int.TryParse(MaxActiveTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxActive) || maxActive < 0)
        {
            ShowSettingsValidationError("Maximum active downloads must be 0 or a positive number.");
            return;
        }

        var updated = _viewModel.Settings.Copy();
        updated.DefaultDownloadFolder = DefaultDownloadFolderTextBox.Text ?? string.Empty;
        updated.MaximumActiveDownloads = maxActive;
        updated.ListenPort = listenPort;
        updated.ListenAddress = ListenAddressTextBox.Text?.Trim() ?? string.Empty;
        updated.EnableDht = DhtCheckBox.IsChecked == true;
        updated.EnablePeerExchange = PexCheckBox.IsChecked == true;
        updated.EnableLocalPeerDiscovery = LsdCheckBox.IsChecked == true;
        updated.EnablePortForwarding = PortForwardingCheckBox.IsChecked == true;
        updated.RequireEncryptedPeerConnections = EncryptionCheckBox.IsChecked == true;
        updated.UsePublicMetadataCaches = PublicCachesCheckBox.IsChecked == true;
        updated.MaximumDownloadKiBPerSecond = downloadLimit;
        updated.MaximumUploadKiBPerSecond = uploadLimit;
        updated.CloseToTray = CloseToTrayCheckBox.IsChecked == true;
        updated.StartWithWindows = StartWithWindowsCheckBox.IsChecked == true;
        updated.Theme = ThemeComboBox.SelectedIndex switch { 1 => "Light", 2 => "Dark", _ => "System" };
        updated.Normalize();

        try
        {
            await _viewModel.SaveSettingsAsync(updated);
            ValidationErrorTextBlock.Text = string.Empty;
            ValidationErrorTextBlock.IsVisible = false;
            _viewModel.IsSettingsOpen = false;
        }
        catch (Exception exception)
        {
            ShowSettingsValidationError($"Could not save settings: {exception.Message}");
            _viewModel.NotifyActionFailure($"Could not save settings: {exception.Message}");
        }
    }

    private void ShowSettingsValidationError(string message)
    {
        ValidationErrorTextBlock.Text = message;
        ValidationErrorTextBlock.IsVisible = true;
    }

    private async Task RunActionAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            Services.AppLogger.LogException("Torrent action failed", exception);
            _viewModel.NotifyActionFailure(exception.Message);
        }
    }

    public void ExitForUpdate()
    {
        _exitRequested = true;
        _skipExitConfirmation = true;
        _viewModel.StatusMessage = "Installing update — the app will restart.";
        Show();
        Activate();
        Close();
    }

    public void RequestExit()
    {
        _exitRequested = true;
        Show();
        Activate();
        Close();
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs eventArgs)
    {
        if (_allowClose)
        {
            return;
        }

        if (!_exitRequested && _viewModel.Settings.CloseToTray)
        {
            eventArgs.Cancel = true;
            Hide();
            _viewModel.StatusMessage = "Running in the background — right-click the tray icon to exit.";
            return;
        }

        eventArgs.Cancel = true;
        if (_shutdownStarted)
        {
            return;
        }

        if (_viewModel.Settings.ConfirmCloseBeforeExit && !_skipExitConfirmation)
        {
            var choice = await new ExitConfirmationWindow() { Icon = Icon }.ShowDialog<ExitChoice?>(this);
            if (choice is null)
            {
                return;
            }

            if (choice.RememberChoice)
            {
                await _viewModel.SaveExitPromptPreferenceAsync(shouldConfirm: false);
            }
        }

        _shutdownStarted = true;
        _refreshTimer.Stop();
        await _viewModel.ShutdownAsync();
        _allowClose = true;
        Close();
    }
}
