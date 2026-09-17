using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace FastTorrentDownload.Views;

public sealed record AddTorrentRequest(string Source, string? Destination, bool StartImmediately);

public partial class AddTorrentWindow : Window
{
    public AddTorrentWindow(string defaultDestination)
    {
        InitializeComponent();
        DestinationTextBox.Text = defaultDestination;
    }

    private async void BrowseTorrent_Click(object? sender, RoutedEventArgs eventArgs)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a torrent file",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Torrent files") { Patterns = ["*.torrent"] }]
        });

        if (files.Count > 0)
        {
            SourceTextBox.Text = files[0].TryGetLocalPath() ?? files[0].Path.LocalPath;
        }
    }

    private async void ChooseFolder_Click(object? sender, RoutedEventArgs eventArgs)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose download folder",
            AllowMultiple = false
        });

        if (folders.Count > 0)
        {
            DestinationTextBox.Text = folders[0].TryGetLocalPath() ?? folders[0].Path.LocalPath;
        }
    }

    private async void Drop_Handle(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer is not IAsyncDataTransfer transfer)
        {
            return;
        }

        var files = await transfer.TryGetFilesAsync();
        var path = files?.Select(file => file.TryGetLocalPath()).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
        var text = (string.IsNullOrWhiteSpace(path) ? await transfer.TryGetTextAsync() : path) ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(text))
        {
            SourceTextBox.Text = text.Trim();
        }
    }

    private void Add_Click(object? sender, RoutedEventArgs eventArgs) =>
        Close(new AddTorrentRequest(SourceTextBox.Text ?? string.Empty, DestinationTextBox.Text, StartImmediatelyCheckBox.IsChecked == true));

    private void Cancel_Click(object? sender, RoutedEventArgs eventArgs) => Close(null);
}
