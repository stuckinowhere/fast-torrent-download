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
    public SelectTorrentFilesWindow(string torrentName)
    {
        Files = new ObservableCollection<TorrentFileSelectionItem>();
        DataContext = this;
        InitializeComponent();
        TorrentNameTextBlock.Text = torrentName;
    }

    public ObservableCollection<TorrentFileSelectionItem> Files { get; }

    public void ShowPreview(TorrentAddPreview preview)
    {
        if (!IsVisible)
        {
            return;
        }

        Files.Clear();
        foreach (var file in preview.Files)
        {
            Files.Add(new TorrentFileSelectionItem(file));
        }

        TorrentNameTextBlock.Text = preview.Name;
        LoadingPanel.IsVisible = false;
        FilesListBox.IsVisible = true;
        SelectAllButton.IsEnabled = true;
        SelectNoneButton.IsEnabled = true;
        AddSelectedButton.IsEnabled = true;
    }

    public void FailAndClose()
    {
        if (!IsVisible)
        {
            return;
        }

        Close(null);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs eventArgs) => Close(null);

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
