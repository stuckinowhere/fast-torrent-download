using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using FastTorrentDownload.Services;

namespace FastTorrentDownload.Views;

public partial class UpdateAvailableWindow : Window
{
    private readonly AvailableUpdate _update;
    private readonly AppUpdateService _updateService;
    private readonly CancellationTokenSource _fetch = new();
    private bool _installing;

    public UpdateAvailableWindow(AvailableUpdate update, AppUpdateService updateService)
    {
        _update = update;
        _updateService = updateService;
        InitializeComponent();
        Loc.Apply(this);
        DescriptionTextBlock.Text = Loc.I($"{update.Tag} is newer than the installed version.");
        InstallButton.Content = Loc.I($"Install {update.Tag}");
        Closing += (_, _) => _fetch.Cancel();
    }

    private async void Install_Click(object? sender, RoutedEventArgs eventArgs)
    {
        if (_installing)
        {
            return;
        }

        _installing = true;
        InstallButton.IsEnabled = false;
        LaterButton.IsEnabled = false;
        ErrorTextBlock.IsVisible = false;
        ProgressPanel.IsVisible = true;
        SetStatus(Loc.T("Downloading update…"), 0);
        try
        {
            var progress = new Progress<double>(value =>
                Dispatcher.UIThread.Post(() => SetStatus(Loc.T("Downloading update…"), value * 100)));
            var packagePath = await _updateService.DownloadAndVerifyAsync(_update, progress, _fetch.Token);
            Dispatcher.UIThread.Post(() => SetStatus(Loc.T("Verified. Staging install…"), 100));
            await Task.Run(() => _updateService.StageAndLaunchInstaller(packagePath, _update.Tag), _fetch.Token);
            Dispatcher.UIThread.Post(() => SetStatus(Loc.T("Restarting on the new version…"), 100));
            await Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None);
            Close(true);
        }
        catch (OperationCanceledException)
        {
            Close(false);
        }
        catch (Exception exception)
        {
            AppLogger.LogException($"Update to {_update.Tag} failed", exception);
            ErrorTextBlock.Text = Loc.I($"Could not install the update: {exception.Message}");
            ErrorTextBlock.IsVisible = true;
            InstallButton.IsEnabled = true;
            LaterButton.IsEnabled = true;
            _installing = false;
        }
    }

    private void Later_Click(object? sender, RoutedEventArgs eventArgs) => Close(false);

    private void SetStatus(string message, double percent)
    {
        StatusTextBlock.Text = percent is > 0 and < 100 ? $"{message} {percent:0}%" : message;
        DownloadProgressBar.Value = Math.Clamp(percent, 0, 100);
    }
}
