using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using FastTorrentDownload.Models;
using FastTorrentDownload.Services;

namespace FastTorrentDownload.ViewModels;

public sealed partial class MainViewModel(
    TorrentEngineService engine,
    SettingsStore settingsStore,
    ReleaseUpdateService updateService) : ObservableObject
{
    private readonly TorrentEngineService _engine = engine;
    private readonly SettingsStore _settingsStore = settingsStore;
    private readonly ReleaseUpdateService _updateService = updateService;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly HashSet<string> _rowOperations = new();
    private readonly Dictionary<string, int> _closedPortStreaks = new();
    private readonly HashSet<string> _notifiedErrors = new();
    private bool _portWarningDismissed;
    private bool _portWarningNotified;
    private AppSettings _settings = new();

    public ObservableCollection<TorrentRowViewModel> Torrents { get; } = [];
    public ObservableCollection<ActivityEntry> Activity { get; } = [];
    private DateTimeOffset _notificationUntil;
    [ObservableProperty] private ActivityEntry? _notification;
    [ObservableProperty] private bool _hasPortWarning;
    [ObservableProperty] private string _portWarningMessage = string.Empty;
    [ObservableProperty] private bool _isSettingsOpen;
    public bool IsQueueOpen => !IsSettingsOpen;
    public bool HasNotification => Notification is not null;
    public bool CanOpenNotificationFolder => Notification?.Destination is not null;
    public bool HasTorrents => Torrents.Count > 0;
    partial void OnIsSettingsOpenChanged(bool value) => OnPropertyChanged(nameof(IsQueueOpen));
    partial void OnNotificationChanged(ActivityEntry? value)
    {
        OnPropertyChanged(nameof(HasNotification));
        OnPropertyChanged(nameof(CanOpenNotificationFolder));
    }
    partial void OnStatusMessageChanged(string value)
    {
        if (value == "Ready.") return;
        Activity.Insert(0, new ActivityEntry(DateTimeOffset.Now, value));
        while (Activity.Count > 50) Activity.RemoveAt(Activity.Count - 1);
    }

    [ObservableProperty] private TorrentRowViewModel? _selectedTorrent;
    [ObservableProperty] private string _statusMessage = "Starting torrent engine…";
    [ObservableProperty] private string _totalDownloadRate = "↓ 0 B/s";
    [ObservableProperty] private string _totalUploadRate = "↑ 0 B/s";
    [ObservableProperty] private string _queueSummary = "0 torrents";

    public AppSettings Settings => _settings;

    public string AppVersionLabel
    {
        get
        {
            var version = typeof(MainViewModel).Assembly.GetName().Version ?? new Version(0, 1, 0);
            return $"v{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    public async Task InitializeAsync()
    {
        _settings = await _settingsStore.LoadAsync();
        ApplyTheme(_settings.Theme);
        await _engine.InitializeAsync(_settings);
        await RefreshAsync();
        StatusMessage = "Ready — add a magnet link or .torrent file.";
    }

    public Task<TorrentAddPreview> PrepareAddAsync(string source, string? destination, CancellationToken cancellationToken = default) =>
        _engine.PrepareAddAsync(source, destination, cancellationToken);

    public async Task AddAsync(TorrentAddPreview preview, IReadOnlyCollection<string> selectedFiles, bool startImmediately)
    {
        await _engine.CommitAddAsync(preview, selectedFiles, startImmediately);
        await SyncPauseIntentsAsync();
        await RefreshAsync();
        StatusMessage = startImmediately
            ? $"Added and started {preview.Name}."
            : $"Added {preview.Name} in a paused state.";
        ShowNotification(StatusMessage, null);
    }

    public void DismissPortWarning()
    {
        _portWarningDismissed = true;
        HasPortWarning = false;
    }

    public void NotifyActionFailure(string message)
    {
        StatusMessage = message;
        ShowNotification(message, null);
    }

    public Task CancelAddAsync(string id) => _engine.CancelAddAsync(id);

    public async Task StartSelectedAsync()
    {
        if (SelectedTorrent is null)
        {
            StatusMessage = "Select a torrent first.";
            return;
        }

        var row = SelectedTorrent;
        await RunRowActionAsync(row, "Starting…", () => _engine.StartAsync(row.Id), $"Starting {row.Name}…", $"Started {row.Name}.");
    }

    public async Task PauseTorrentAsync(string id)
    {
        var row = Torrents.FirstOrDefault(current => current.Id == id);
        if (row is not null)
            await RunRowActionAsync(row, "Stopping…", () => _engine.PauseAsync(id), $"Stopping {row.Name}…", $"Paused {row.Name}.");
    }

    public async Task RecheckSelectedAsync()
    {
        if (SelectedTorrent is null)
        {
            StatusMessage = "Select a torrent first.";
            return;
        }

        var row = SelectedTorrent;
        await RunRowActionAsync(row, "Rechecking…", () => _engine.RecheckAsync(row.Id), $"Rechecking {row.Name}…", $"Recheck started for {row.Name}.");
    }

    public async Task RemoveSelectedAsync()
    {
        if (SelectedTorrent is null)
        {
            StatusMessage = "Select a torrent first.";
            return;
        }

        var row = SelectedTorrent;
        await RunRowActionAsync(row, "Stopping…", () => _engine.RemoveAsync(row.Id),
            $"Stopping {row.Name} before removing…", $"Removed {row.Name}; downloaded files were kept.");
    }

    private async Task RunRowActionAsync(TorrentRowViewModel row, string operation, Func<Task> action, string pending, string success)
    {
        if (row.IsBusy) return;
        _rowOperations.Add(row.Id);
        row.Operation = operation;
        StatusMessage = pending;
        try
        {
            await action();
            await SyncPauseIntentsAsync();
            await RefreshAsync(waitForPending: true);
            StatusMessage = success;
            ShowNotification(success, operation == "Stopping…" && !Torrents.Contains(row) ? row.Destination : null);
        }
        finally
        {
            _rowOperations.Remove(row.Id);
            row.Operation = null;
        }
    }

    public void OpenSelectedFolder()
    {
        var folder = ResolveFolderToOpen(SelectedTorrent, _settings);
        if (folder is null)
        {
            StatusMessage = "That download folder is not available.";
            return;
        }

        OpenFolder(folder);
        if (SelectedTorrent is null) StatusMessage = "Opened the default download folder.";
    }

    public void OpenNotificationFolder()
    {
        if (Notification?.Destination is { } folder) OpenFolder(folder);
    }

    private void OpenFolder(string folder)
    {
        if (!Directory.Exists(folder))
        {
            StatusMessage = "That download folder is not available.";
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            StatusMessage = $"Could not open the folder: {exception.Message}";
        }
    }

    public static string? ResolveFolderToOpen(TorrentRowViewModel? selected, AppSettings settings)
    {
        var candidate = string.IsNullOrWhiteSpace(selected?.Destination)
            ? settings.DefaultDownloadFolder
            : selected!.Destination;
        return Directory.Exists(candidate) ? candidate : null;
    }

    private async Task SyncPauseIntentsAsync()
    {
        _settings.PausedInfoHashes = new HashSet<string>(_engine.PausedInfoHashes, StringComparer.OrdinalIgnoreCase);
        await _settingsStore.SaveAsync(_settings);
    }

    public async Task SaveSettingsAsync(AppSettings updatedSettings)
    {
        updatedSettings.Normalize();
        updatedSettings.PausedInfoHashes = new HashSet<string>(_engine.PausedInfoHashes, StringComparer.OrdinalIgnoreCase);
        await _settingsStore.SaveAsync(updatedSettings);
        await _engine.ApplySettingsAsync(updatedSettings);
        _settings = updatedSettings.Copy();
        ApplyTheme(_settings.Theme);
        StatusMessage = "Settings saved.";
        ShowNotification(StatusMessage, null);
    }

    public async Task SaveExitPromptPreferenceAsync(bool shouldConfirm)
    {
        _settings.ConfirmCloseBeforeExit = shouldConfirm;
        await _settingsStore.SaveAsync(_settings);
    }

    public async Task<AvailableUpdate?> CheckForUpdatesAsync(bool quiet)
    {
        try
        {
            var currentVersion = typeof(MainViewModel).Assembly.GetName().Version ?? new Version(0, 1, 0);
            var update = await _updateService.CheckAsync(currentVersion);
            if (update is not null)
            {
                StatusMessage = $"Update {update.Tag} is available.";
            }
            else if (!quiet)
            {
                StatusMessage = "You have the latest release.";
            }

            return update;
        }
        catch (Exception) when (quiet)
        {
            return null;
        }
        catch (Exception)
        {
            StatusMessage = "Could not check GitHub releases. Check your connection and try again.";
            return null;
        }
    }

    public void SetTheme(string theme)
    {
        if (theme is not ("Light" or "Dark"))
        {
            return;
        }

        _settings.Theme = theme;
        ApplyTheme(theme);
        _ = _settingsStore.SaveAsync(_settings);
        StatusMessage = $"{theme} theme enabled.";
    }

    public async Task RefreshAsync(bool waitForPending = false)
    {
        if (waitForPending)
        {
            // Actions need a fresh snapshot after any older refresh has finished.
            await _refreshGate.WaitAsync();
        }
        else if (!await _refreshGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            ExpireNotification(DateTimeOffset.UtcNow);
            var snapshots = await _engine.GetSnapshotsAsync(_rowOperations.ToHashSet());
            var rowsById = Torrents.ToDictionary(row => row.Id);
            foreach (var snapshot in snapshots)
            {
                if (rowsById.Remove(snapshot.Id, out var row))
                {
                    var localOperation = _rowOperations.Contains(row.Id) ? row.Operation : null;
                    row.Update(snapshot);
                    if (localOperation is not null) row.Operation = localOperation;
                }
                else
                {
                    Torrents.Add(new TorrentRowViewModel(snapshot));
                }
            }

            foreach (var obsolete in rowsById.Values)
            {
                if (SelectedTorrent == obsolete)
                {
                    SelectedTorrent = null;
                }

                _closedPortStreaks.Remove(obsolete.Id);
                _notifiedErrors.RemoveWhere(key => key.StartsWith(obsolete.Id + ":", StringComparison.Ordinal));
                Torrents.Remove(obsolete);
            }

            var totalDownload = snapshots.Sum(snapshot => snapshot.DownloadRate);
            var totalUpload = snapshots.Sum(snapshot => snapshot.UploadRate);
            TotalDownloadRate = $"↓ {TorrentRowViewModel.FormatRate(totalDownload)}";
            TotalUploadRate = $"↑ {TorrentRowViewModel.FormatRate(totalUpload)}";
            QueueSummary = snapshots.Count == 1 ? "1 torrent" : $"{snapshots.Count} torrents";
            OnPropertyChanged(nameof(HasTorrents));
            NotifySnapshotErrors(snapshots);
            UpdatePortWarning(snapshots);
            foreach (var completion in _engine.TakeCompletions())
            {
                StatusMessage = completion.Error is null
                    ? $"Completed {completion.Name}; downloaded files were kept."
                    : $"Could not finish removing {completion.Name}: {completion.Error}";
                ShowNotification(StatusMessage, completion.Error is null ? completion.Destination : null);
            }
        }
        catch (Exception exception)
        {
            StatusMessage = $"Could not refresh the queue: {exception.Message}";
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public Task ShutdownAsync() => _engine.DisposeAsync().AsTask();

    private void NotifySnapshotErrors(IReadOnlyList<TorrentSnapshot> snapshots)
    {
        foreach (var snapshot in snapshots)
        {
            var error = snapshot.Diagnostics?.TorrentError ?? (snapshot.State == "Error" ? "Torrent stopped due to an error." : null);
            var prefix = snapshot.Id + ":";
            if (string.IsNullOrWhiteSpace(error))
            {
                _notifiedErrors.RemoveWhere(key => key.StartsWith(prefix, StringComparison.Ordinal));
                continue;
            }

            var key = prefix + error;
            if (_notifiedErrors.Add(key))
            {
                StatusMessage = $"Error in {snapshot.Name}: {error}";
                ShowNotification(StatusMessage, snapshot.Destination);
            }
        }
    }

    private void UpdatePortWarning(IReadOnlyList<TorrentSnapshot> snapshots)
    {
        if (snapshots.Any(snapshot => snapshot.Diagnostics?.IncomingPeers > 0))
        {
            _closedPortStreaks.Clear();
            HasPortWarning = false;
            return;
        }

        var anyDownloading = false;
        var shouldWarn = false;
        foreach (var snapshot in snapshots)
        {
            if (snapshot.State != "Downloading")
            {
                _closedPortStreaks.Remove(snapshot.Id);
                continue;
            }

            anyDownloading = true;
            if (snapshot.Diagnostics?.IsInboundPortLikelyClosed == true)
            {
                var streak = _closedPortStreaks.GetValueOrDefault(snapshot.Id) + 1;
                _closedPortStreaks[snapshot.Id] = streak;
                if (streak >= 5)
                {
                    shouldWarn = true;
                }
            }
            else
            {
                _closedPortStreaks.Remove(snapshot.Id);
            }
        }

        if (!anyDownloading)
        {
            HasPortWarning = false;
            return;
        }

        if (shouldWarn)
        {
            PortWarningMessage = $"Inbound port {_settings.ListenPort} (TCP/UDP) appears closed (0 incoming peers). Open it in your router for much faster downloads.";
            if (!_portWarningDismissed)
            {
                HasPortWarning = true;
            }

            if (!_portWarningNotified)
            {
                _portWarningNotified = true;
                StatusMessage = $"Inbound port {_settings.ListenPort} appears closed (0 incoming peers) — click 'How to open port' to boost speed.";
                ShowNotification(StatusMessage, null);
            }
        }
    }

    private void ShowNotification(string message, string? destination)
    {
        Notification = new ActivityEntry(DateTimeOffset.Now, message, destination);
        if (Activity.Count > 0 && Activity[0].Message == message) Activity[0] = Notification;
        _notificationUntil = DateTimeOffset.UtcNow.AddSeconds(8);
    }

    public void ExpireNotification(DateTimeOffset now)
    {
        if (Notification is not null && now >= _notificationUntil)
        {
            if (StatusMessage == Notification.Message) StatusMessage = "Ready.";
            Notification = null;
        }
    }

    private static void ApplyTheme(string theme)
    {
        if (Application.Current is not null)
        {
            Application.Current.RequestedThemeVariant = theme switch
            {
                "Light" => ThemeVariant.Light,
                "Dark" => ThemeVariant.Dark,
                _ => ThemeVariant.Default
            };
        }
    }
}
