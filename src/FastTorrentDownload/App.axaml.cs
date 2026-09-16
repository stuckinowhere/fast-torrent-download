using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using FastTorrentDownload.Services;
using FastTorrentDownload.ViewModels;
using FastTorrentDownload.Views;

namespace FastTorrentDownload;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var paths = new AppPaths();
            var settings = new SettingsStore(paths);
            var engine = new TorrentEngineService(paths);
            var mainWindow = new MainWindow(new MainViewModel(engine, settings, new ReleaseUpdateService()));
            try
            {
                mainWindow.Icon = CreateAppIcon();
            }
            catch
            {
                // A window without a custom icon is preferable to failing startup.
            }

            desktop.MainWindow = mainWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static WindowIcon CreateAppIcon()
    {
        const string pngBase64 =
            "iVBORw0KGgoAAAANSUhEUgAAACAAAAAgCAYAAABzenr0AAAASUlEQVR4nO3WMQoAMAgEwfv/p02fJokiGtkDS2VACyXJigsAAF/jHgAAAAAAMB/wGgDzALeQtCMEcIJ45wDo/5ZH8z+gfAXZtQAzgOZgXcu3jQAAAABJRU5ErkJggg==";
        return new WindowIcon(new MemoryStream(Convert.FromBase64String(pngBase64)));
    }
}
