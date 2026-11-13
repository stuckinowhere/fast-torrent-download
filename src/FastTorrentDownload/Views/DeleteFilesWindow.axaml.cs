using FastTorrentDownload.Services;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace FastTorrentDownload.Views;

public partial class DeleteFilesWindow : Window
{
    public DeleteFilesWindow(string torrentName, string folder, string sizeLabel)
    {
        InitializeComponent();
        Loc.Apply(this);
        NameTextBlock.Text = torrentName;
        DetailTextBlock.Text = Loc.I($"{sizeLabel} verified so far · {folder}");
    }

    private void Delete_Click(object? sender, RoutedEventArgs eventArgs) => Close(true);
    private void Cancel_Click(object? sender, RoutedEventArgs eventArgs) => Close(false);
}
