using Avalonia.Controls;
using Avalonia.Interactivity;
using FastTorrentDownload.Services;

namespace FastTorrentDownload.Views;

public partial class UpdateAvailableWindow : Window
{
    public UpdateAvailableWindow(AvailableUpdate update)
    {
        InitializeComponent();
        DescriptionTextBlock.Text = $"{update.Tag} is newer than the installed version."
            + " This app never downloads or installs an update silently.";
    }

    private void Open_Click(object? sender, RoutedEventArgs eventArgs) => Close(true);
    private void Later_Click(object? sender, RoutedEventArgs eventArgs) => Close(false);
}
