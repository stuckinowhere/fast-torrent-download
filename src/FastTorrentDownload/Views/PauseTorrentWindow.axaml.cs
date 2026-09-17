using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace FastTorrentDownload.Views;

public sealed class PausableTorrentItem
{
    public PausableTorrentItem(string id, string name, string state)
    {
        Id = id;
        Name = name;
        State = state;
    }

    public string Id { get; }
    public string Name { get; }
    public string State { get; }
}

public partial class PauseTorrentWindow : Window
{
    public PauseTorrentWindow(IReadOnlyList<PausableTorrentItem> torrents, string? selectedId)
    {
        Torrents = new ObservableCollection<PausableTorrentItem>(torrents);
        DataContext = this;
        InitializeComponent();
        TorrentsListBox.SelectedItem =
            Torrents.FirstOrDefault(item => item.Id == selectedId) ?? Torrents.FirstOrDefault();
    }

    public ObservableCollection<PausableTorrentItem> Torrents { get; }

    private void Cancel_Click(object? sender, RoutedEventArgs eventArgs) => Close(null);

    private void Pause_Click(object? sender, RoutedEventArgs eventArgs) =>
        Close((TorrentsListBox.SelectedItem as PausableTorrentItem)?.Id);
}
