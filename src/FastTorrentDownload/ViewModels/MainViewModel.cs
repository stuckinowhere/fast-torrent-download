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
    private AppSettings _settings = new();

    public ObservableCollection<TorrentRowViewModel> Torrents { get; } = [];

    [ObservableProperty] private TorrentRowViewModel? _selectedTorrent;
    [ObservableProperty] private string _statusMessage = "Starting torrent engine…";
    [ObservableProperty] private string _totalDownloadRate = "↓ 0 B/s";
    [ObservableProperty] private string _totalUploadRate = "↑ 0 B/s";
    [ObservableProperty] private string _queueSummary = "No torrents";

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
        // Update prompts are driven by the main window so a dialog can be shown.
    }

    public Task<TorrentAddPreview> PrepareAddAsync(string source, string? destination, CancellationToken cancellationToken = default) =>
        _engine.PrepareAddAsync(source, destination, cancellationToken);

    public async Task AddAsync(TorrentAddPreview preview, IReadOnlyCollection<string> selectedFiles, bool startImmediately)
    {
        await _engine.CommitAddAsync(preview, selectedFiles, startImmediately);
        await RefreshAsync();
        StatusMessage = startImmediately ? "Added and started." : "Added in a paused state.";
    }

    public Task CancelAddAsync(string id) => _engine.CancelAddAsync(id);

    public async Task StartSelectedAsync()
    {
        if (SelectedTorrent is null)
        {
            StatusMessage = "Select a torrent first.";
            return;
        }

        await _engine.StartAsync(SelectedTorrent.Id);
        await RefreshAsync();
    }

    public async Task PauseTorrentAsync(string id)
    {
        await _engine.PauseAsync(id);
        await RefreshAsync();
        var row = Torrents.FirstOrDefault(current => current.Id == id);
        SelectedTorrent = row;
        StatusMessage = row is null ? "Paused." : $"Paused {row.Name}.";
    }

    public async Task RecheckSelectedAsync()
    {
        if (SelectedTorrent is null)
        {
            StatusMessage = "Select a torrent first.";
            return;
        }

        StatusMessage = $"Rechecking {SelectedTorrent.Name}…";
        await _engine.RecheckAsync(SelectedTorrent.Id);
        await RefreshAsync();
    }

    public async Task RemoveSelectedAsync()
    {
        if (SelectedTorrent is null)
        {
            StatusMessage = "Select a torrent first.";
            return;
        }

        var name = SelectedTorrent.Name;
        await _engine.RemoveAsync(SelectedTorrent.Id);
        SelectedTorrent = null;
        await RefreshAsync();
        StatusMessage = $"Removed {name}; downloaded files were kept.";
    }

    public void OpenSelectedFolder()
    {
        var folder = ResolveFolderToOpen(SelectedTorrent, _settings);
        if (folder is null)
        {
            StatusMessage = "That download folder is not available.";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
            if (SelectedTorrent is null)
            {
                StatusMessage = "No torrent selected — opened the default download folder.";
            }
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

    public async Task SaveSettingsAsync(AppSettings updatedSettings)
    {
        updatedSettings.Normalize();
        await _settingsStore.SaveAsync(updatedSettings);
        await _engine.ApplySettingsAsync(updatedSettings);
        _settings = updatedSettings.Copy();
        ApplyTheme(_settings.Theme);
        StatusMessage = "Settings saved.";
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
            var update = await _updateService.CheckAsync(_settings.UpdateRepository, currentVersion);
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

    public async Task RefreshAsync()
    {
        if (!await _refreshGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            await _engine.EnforceRatioPolicyAsync();
            var snapshots = await _engine.GetSnapshotsAsync();
            var rowsById = Torrents.ToDictionary(row => row.Id);
            foreach (var snapshot in snapshots)
            {
                if (rowsById.Remove(snapshot.Id, out var row))
                {
                    row.Update(snapshot);
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

                Torrents.Remove(obsolete);
            }

            var totalDownload = snapshots.Sum(snapshot => snapshot.DownloadRate);
            var totalUpload = snapshots.Sum(snapshot => snapshot.UploadRate);
            TotalDownloadRate = $"↓ {TorrentRowViewModel.FormatRate(totalDownload)}";
            TotalUploadRate = $"↑ {TorrentRowViewModel.FormatRate(totalUpload)}";
            QueueSummary = snapshots.Count == 1 ? "1 torrent" : $"{snapshots.Count} torrents";
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
