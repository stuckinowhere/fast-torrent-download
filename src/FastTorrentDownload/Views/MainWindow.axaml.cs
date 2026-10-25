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
    private bool _allowClose;
    private bool _shutdownStarted;
    private bool _exitRequested;
    private bool _skipExitConfirmation;

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
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
            await _viewModel.InitializeAsync();
            _refreshTimer.Start();
            await ShowUpdateDialogIfAvailableAsync(quiet: true);
        }
        catch (Exception exception)
        {
            _viewModel.NotifyActionFailure($"Could not start the torrent engine: {exception.Message}");
        }
    }

    private async void OnRefreshTick(object? sender, EventArgs eventArgs) => await _viewModel.RefreshAsync();

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

        var text = (await Clipboard.TryGetTextAsync())?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var isMagnet = text.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase);
        if (!isMagnet && !string.Equals(Path.GetExtension(text), ".torrent", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        e.Handled = true;
        await AddTorrentAsync(new AddTorrentRequest(text, null, StartImmediately: true));
    }

    private async void Drop_Handle(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer is not IAsyncDataTransfer transfer)
        {
            return;
        }

        var files = await transfer.TryGetFilesAsync();
        var path = files?.Select(file => file.TryGetLocalPath()).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
        var source = (string.IsNullOrWhiteSpace(path) ? await transfer.TryGetTextAsync() : path) ?? string.Empty;
        var trimmed = source.Trim();
        var isMagnet = trimmed.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase);
        if (!isMagnet && !string.Equals(Path.GetExtension(trimmed), ".torrent", StringComparison.OrdinalIgnoreCase))
        {
            _viewModel.StatusMessage = "Drop a .torrent file here, or add a magnet link.";
            return;
        }

        _viewModel.IsSettingsOpen = false;
        await AddTorrentAsync(new AddTorrentRequest(trimmed, null, StartImmediately: true));
    }

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

    private async void Resume_Click(object? sender, RoutedEventArgs eventArgs) => await RunActionAsync(_viewModel.StartSelectedAsync);
    private async void Pause_Click(object? sender, RoutedEventArgs eventArgs)
    {
        if (_viewModel.Torrents.Count == 0)
        {
            _viewModel.StatusMessage = "No torrents to pause.";
            return;
        }

        var items = _viewModel.Torrents
            .Where(row => !row.IsBusy)
            .Select(row => new PausableTorrentItem(row.Id, row.Name, row.State))
            .ToArray();
        if (items.Length == 0)
        {
            _viewModel.StatusMessage = "Wait for the current torrent operations to finish.";
            return;
        }
        var id = await new PauseTorrentWindow(items, _viewModel.SelectedTorrent?.Id) { Icon = Icon }.ShowDialog<string?>(this);
        if (id is null)
        {
            return;
        }

        await RunActionAsync(() => _viewModel.PauseTorrentAsync(id));
    }
    private async void Recheck_Click(object? sender, RoutedEventArgs eventArgs) => await RunActionAsync(_viewModel.RecheckSelectedAsync);

    private void Folder_Click(object? sender, RoutedEventArgs eventArgs) => _viewModel.OpenSelectedFolder();
    private void NotificationFolder_Click(object? sender, RoutedEventArgs eventArgs) => _viewModel.OpenNotificationFolder();
    private async void PortGuide_Click(object? sender, RoutedEventArgs eventArgs)
    {
        var info = Services.NetworkBinding.DetectLocalNetworkInfo(_viewModel.Settings.ListenPort);
        await new PortForwardingGuideWindow(info) { Icon = Icon }.ShowDialog(this);
    }

    private void DismissPortWarning_Click(object? sender, RoutedEventArgs eventArgs) => _viewModel.DismissPortWarning();

    private async void Activity_Click(object? sender, RoutedEventArgs eventArgs) =>
        await new ActivityWindow(_viewModel.Activity) { Icon = Icon }.ShowDialog(this);

    private async void Remove_Click(object? sender, RoutedEventArgs eventArgs) => await RunActionAsync(_viewModel.RemoveSelectedAsync);

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
        ListenPortTextBox.Text = copy.ListenPort.ToString(CultureInfo.InvariantCulture);
        ListenAddressTextBox.Text = copy.ListenAddress;
        DhtCheckBox.IsChecked = copy.EnableDht;
        PexCheckBox.IsChecked = copy.EnablePeerExchange;
        LsdCheckBox.IsChecked = copy.EnableLocalPeerDiscovery;
        PortForwardingCheckBox.IsChecked = copy.EnablePortForwarding;
        EncryptionCheckBox.IsChecked = copy.RequireEncryptedPeerConnections;
        DownloadLimitTextBox.Text = copy.MaximumDownloadKiBPerSecond.ToString(CultureInfo.InvariantCulture);
        UploadLimitTextBox.Text = copy.MaximumUploadKiBPerSecond.ToString(CultureInfo.InvariantCulture);
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

        var updated = _viewModel.Settings.Copy();
        updated.DefaultDownloadFolder = DefaultDownloadFolderTextBox.Text ?? string.Empty;
        updated.ListenPort = listenPort;
        updated.ListenAddress = ListenAddressTextBox.Text?.Trim() ?? string.Empty;
        updated.EnableDht = DhtCheckBox.IsChecked == true;
        updated.EnablePeerExchange = PexCheckBox.IsChecked == true;
        updated.EnableLocalPeerDiscovery = LsdCheckBox.IsChecked == true;
        updated.EnablePortForwarding = PortForwardingCheckBox.IsChecked == true;
        updated.RequireEncryptedPeerConnections = EncryptionCheckBox.IsChecked == true;
        updated.MaximumDownloadKiBPerSecond = downloadLimit;
        updated.MaximumUploadKiBPerSecond = uploadLimit;
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

        if (!_exitRequested)
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
