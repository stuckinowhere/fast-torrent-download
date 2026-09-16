using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CommunityToolkit.Mvvm.ComponentModel;
using FastTorrentDownload.Models;

namespace FastTorrentDownload.Views;

public sealed partial class TorrentFileSelectionItem : ObservableObject
{
    public TorrentFileSelectionItem(TorrentFilePreview file)
    {
        Path = file.Path;
        SizeLabel = FormatSize(file.Length);
    }

    public string Path { get; }
    public string SizeLabel { get; }
    [ObservableProperty]
    private bool _isSelected = true;

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.##} {units[unit]}";
    }
}

public partial class SelectTorrentFilesWindow : Window
{
    public SelectTorrentFilesWindow(TorrentAddPreview preview)
    {
        TorrentName = preview.Name;
        Files = new ObservableCollection<TorrentFileSelectionItem>(preview.Files.Select(file => new TorrentFileSelectionItem(file)));
        DataContext = this;
        InitializeComponent();
    }

    public string TorrentName { get; }
    public ObservableCollection<TorrentFileSelectionItem> Files { get; }
    private void SelectAll_Click(object? sender, RoutedEventArgs eventArgs)
    {
        foreach (var file in Files)
        {
            file.IsSelected = true;
        }
    }

    private void SelectNone_Click(object? sender, RoutedEventArgs eventArgs)
    {
        foreach (var file in Files)
        {
            file.IsSelected = false;
        }
    }

    private void Add_Click(object? sender, RoutedEventArgs eventArgs)
    {
        var selected = Files.Where(file => file.IsSelected).Select(file => file.Path).ToArray();
        if (selected.Length == 0)
        {
            StatusTextBlock.Text = "Select at least one file to download.";
            StatusTextBlock.IsVisible = true;
            return;
        }

        Close(selected);
    }
}
