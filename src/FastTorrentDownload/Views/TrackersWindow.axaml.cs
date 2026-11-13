using Avalonia.Controls;
using Avalonia.Interactivity;
using FastTorrentDownload.Models;

namespace FastTorrentDownload.Views;

public partial class TrackersWindow : Window
{
    public TrackersWindow(string torrentName, IReadOnlyList<TrackerDiagnostic> trackers)
    {
        InitializeComponent();
        NameTextBlock.Text = torrentName;
        CurrentTextBlock.Text = trackers.Count == 0
            ? "This torrent has no trackers; it relies on DHT and peer exchange."
            : string.Join(Environment.NewLine, trackers.Select(tracker =>
                $"{tracker.Address} · {tracker.State}{(string.IsNullOrWhiteSpace(tracker.Message) ? string.Empty : " · " + tracker.Message)}"));
    }

    private void Add_Click(object? sender, RoutedEventArgs eventArgs) => Close(NewTrackersTextBox.Text ?? string.Empty);
    private void Cancel_Click(object? sender, RoutedEventArgs eventArgs) => Close(null);
}
