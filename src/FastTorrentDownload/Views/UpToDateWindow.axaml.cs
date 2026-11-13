using FastTorrentDownload.Services;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace FastTorrentDownload.Views;

public partial class UpToDateWindow : Window
{
    public UpToDateWindow(string installedVersion)
    {
        InitializeComponent();
        Loc.Apply(this);
        DescriptionTextBlock.Text = Loc.I($"Installed version is {installedVersion}, which matches the latest release.");
    }

    private void Ok_Click(object? sender, RoutedEventArgs eventArgs) => Close();
}
