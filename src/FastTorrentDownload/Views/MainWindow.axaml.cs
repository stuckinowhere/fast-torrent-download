using Avalonia.Controls;
using Avalonia.Input;
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
            _viewModel.StatusMessage = $"Could not start the torrent engine: {exception.Message}";
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

        await AddTorrentAsync(request);
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

        await AddTorrentAsync(new AddTorrentRequest(trimmed, null, StartImmediately: true));
    }

    private async Task AddTorrentAsync(AddTorrentRequest request)
    {
        using var fetch = new CancellationTokenSource();
        var previewTask = _viewModel.PrepareAddAsync(request.Source, request.Destination, fetch.Token);
        var picker = new SelectTorrentFilesWindow(PendingTorrentName(request.Source)) { Icon = Icon };
        picker.Closed += (_, _) => fetch.Cancel();
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
            picker.FailAndClose($"Could not load files: {loadError}");
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

            _viewModel.StatusMessage = loadError is null ? "Add cancelled." : $"Could not add torrent: {loadError}";
            return;
        }

        try
        {
            await _viewModel.AddAsync(preview, selectedFiles, request.StartImmediately);
        }
        catch (Exception exception)
        {
            await _viewModel.CancelAddAsync(preview.Id);
            _viewModel.StatusMessage = $"Could not add torrent: {exception.Message}";
        }
    }

    private static string PendingTorrentName(string source)
    {
        var trimmed = source.Trim();
        if (trimmed.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
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
            .Select(row => new PausableTorrentItem(row.Id, row.Name, row.State))
            .ToArray();
        var id = await new PauseTorrentWindow(items, _viewModel.SelectedTorrent?.Id) { Icon = Icon }.ShowDialog<string?>(this);
        if (id is null)
        {
            return;
        }

        await RunActionAsync(() => _viewModel.PauseTorrentAsync(id));
    }
    private async void Recheck_Click(object? sender, RoutedEventArgs eventArgs) => await RunActionAsync(_viewModel.RecheckSelectedAsync);

    private void Folder_Click(object? sender, RoutedEventArgs eventArgs) => _viewModel.OpenSelectedFolder();

    private async void Remove_Click(object? sender, RoutedEventArgs eventArgs) => await RunActionAsync(_viewModel.RemoveSelectedAsync);

    private void ThemeToggle_Click(object? sender, RoutedEventArgs eventArgs)
    {
        var next = Avalonia.Application.Current?.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark ? "Light" : "Dark";
        _viewModel.SetTheme(next);
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
                await new UpToDateWindow(_viewModel.AppVersionLabel) { Icon = Icon }.ShowDialog<bool>(this);
            }

            return;
        }

        var installed = await new UpdateAvailableWindow(update, new Services.AppUpdateService()) { Icon = Icon }.ShowDialog<bool>(this);
        if (installed)
        {
            ExitForUpdate();
        }
    }

    private async void Settings_Click(object? sender, RoutedEventArgs eventArgs)
    {
        var dialog = new SettingsWindow(_viewModel.Settings) { Icon = Icon };
        var settings = await dialog.ShowDialog<Models.AppSettings?>(this);
        if (settings is not null)
        {
            try
            {
                await _viewModel.SaveSettingsAsync(settings);
            }
            catch (Exception exception)
            {
                _viewModel.StatusMessage = $"Could not save settings: {exception.Message}";
            }
        }
    }

    private async Task RunActionAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            _viewModel.StatusMessage = exception.Message;
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
