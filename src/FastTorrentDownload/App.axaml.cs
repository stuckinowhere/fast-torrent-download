using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.Input;
using FastTorrentDownload.Services;
using FastTorrentDownload.ViewModels;
using FastTorrentDownload.Views;

namespace FastTorrentDownload;

public partial class App : Application
{
    private readonly AppPaths _paths;

    public App() : this(new AppPaths()) { }
    public App(AppPaths paths) => _paths = paths;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var paths = _paths;
            AppLogger.Init(paths);
            var settings = new SettingsStore(paths);
            var engine = new TorrentEngineService(paths);
            var viewModel = new MainViewModel(engine, settings, new ReleaseUpdateService());
            var mainWindow = new MainWindow(viewModel);
            WindowIcon? icon = null;
            try
            {
                icon = CreateAppIcon();
                mainWindow.Icon = icon;
            }
            catch
            {
                // A window without a custom icon is preferable to failing startup.
            }

            var trayIcon = new TrayIcon { ToolTipText = viewModel.TrayToolTipText };
            viewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MainViewModel.TrayToolTipText))
                {
                    trayIcon.ToolTipText = viewModel.TrayToolTipText;
                }
            };
            if (icon is not null)
            {
                trayIcon.Icon = icon;
            }

            trayIcon.Command = new RelayCommand(() => ShowMainWindow(mainWindow));
            var menu = new NativeMenu();
            menu.Add(new NativeMenuItem("Open") { Command = trayIcon.Command });
            menu.Add(new NativeMenuItemSeparator());
            menu.Add(new NativeMenuItem("Exit") { Command = new RelayCommand(mainWindow.RequestExit) });
            trayIcon.Menu = menu;
            var icons = new TrayIcons();
            icons.Add(trayIcon);
            TrayIcon.SetIcons(this, icons);

            desktop.MainWindow = mainWindow;
            desktop.Exit += (_, _) => trayIcon.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void ShowMainWindow(MainWindow mainWindow)
    {
        mainWindow.Show();
        mainWindow.Activate();
    }

    private static WindowIcon CreateAppIcon()
    {
        using var asset = AssetLoader.Open(new Uri("avares://FastTorrentDownload/Assets/app.png"));
        return new WindowIcon(asset);
    }
}
