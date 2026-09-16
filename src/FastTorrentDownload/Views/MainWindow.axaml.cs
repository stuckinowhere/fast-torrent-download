using Avalonia.Controls;
using Avalonia.Interactivity;
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

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        Opened += OnOpened;
        Closing += OnClosing;
        _refreshTimer.Tick += OnRefreshTick;
        UpdateThemeButtons(_viewModel.Settings.Theme);
    }

    private async void OnOpened(object? sender, EventArgs eventArgs)
    {
        try
        {
            await _viewModel.InitializeAsync();
            UpdateThemeButtons(_viewModel.Settings.Theme);
            _refreshTimer.Start();
        }
        catch (Exception exception)
        {
            _viewModel.StatusMessage = $"Could not start the torrent engine: {exception.Message}";
        }
    }

    private async void OnRefreshTick(object? sender, EventArgs eventArgs) => await _viewModel.RefreshAsync();

    private async void Add_Click(object? sender, RoutedEventArgs eventArgs)
    {
        var request = await new AddTorrentWindow(_viewModel.Settings.DefaultDownloadFolder).ShowDialog<AddTorrentRequest?>(this);
        if (request is null)
        {
            return;
        }

        TorrentAddPreview? preview = null;
        try
        {
            _viewModel.StatusMessage = "Preparing torrent files…";
            preview = await _viewModel.PrepareAddAsync(request.Source, request.Destination);
            var selectedFiles = await new SelectTorrentFilesWindow(preview).ShowDialog<string[]?>(this);
            if (selectedFiles is null)
            {
                await _viewModel.CancelAddAsync(preview.Id);
                _viewModel.StatusMessage = "Add cancelled.";
                return;
            }

            await _viewModel.AddAsync(preview, selectedFiles, request.StartImmediately);
        }
        catch (Exception exception)
        {
            if (preview is not null)
            {
                await _viewModel.CancelAddAsync(preview.Id);
            }

            _viewModel.StatusMessage = $"Could not add torrent: {exception.Message}";
        }
    }

    private async void Resume_Click(object? sender, RoutedEventArgs eventArgs) => await RunActionAsync(_viewModel.StartSelectedAsync);
    private async void Pause_Click(object? sender, RoutedEventArgs eventArgs) => await RunActionAsync(_viewModel.PauseSelectedAsync);
    private async void Recheck_Click(object? sender, RoutedEventArgs eventArgs) => await RunActionAsync(_viewModel.RecheckSelectedAsync);

    private void Folder_Click(object? sender, RoutedEventArgs eventArgs) => _viewModel.OpenSelectedFolder();

    private async void Remove_Click(object? sender, RoutedEventArgs eventArgs) => await RunActionAsync(_viewModel.RemoveSelectedAsync);

    private void DarkTheme_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _viewModel.SetTheme("Dark");
        UpdateThemeButtons("Dark");
    }

    private void LightTheme_Click(object? sender, RoutedEventArgs eventArgs)
    {
        _viewModel.SetTheme("Light");
        UpdateThemeButtons("Light");
    }

    private void UpdateThemeButtons(string theme)
    {
        DarkThemeButton.Classes.Set("selected", string.Equals(theme, "Dark", StringComparison.OrdinalIgnoreCase));
        LightThemeButton.Classes.Set("selected", string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase));
    }

    private async void Update_Click(object? sender, RoutedEventArgs eventArgs)
    {
        var update = await _viewModel.CheckForUpdatesAsync(quiet: false);
        if (update is null)
        {
            return;
        }

        var shouldOpen = await new UpdateAvailableWindow(update).ShowDialog<bool>(this);
        if (shouldOpen)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(update.ReleasePage.ToString()) { UseShellExecute = true });
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
                UpdateThemeButtons(_viewModel.Settings.Theme);
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

    private async void OnClosing(object? sender, WindowClosingEventArgs eventArgs)
    {
        if (_allowClose)
        {
            return;
        }

        eventArgs.Cancel = true;
        if (_shutdownStarted)
        {
            return;
        }

        if (_viewModel.Settings.ConfirmCloseBeforeExit)
        {
            var choice = await new ExitConfirmationWindow().ShowDialog<ExitChoice?>(this);
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
