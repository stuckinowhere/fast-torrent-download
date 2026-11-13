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
        Length = file.Length;
        SizeLabel = Services.TransferFormatting.Bytes(file.Length);
        _isSelected = file.Selected;
    }

    public string Path { get; }
    public long Length { get; }
    public string SizeLabel { get; }
    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>What the dialog returns: the files to download and whether to fetch them in order.</summary>
public sealed record FileSelection(string[] Files, bool Sequential);

public partial class SelectTorrentFilesWindow : Window
{
    public SelectTorrentFilesWindow(string torrentName, bool editing = false)
    {
        Files = new ObservableCollection<TorrentFileSelectionItem>();
        DataContext = this;
        InitializeComponent();
        TorrentNameTextBlock.Text = torrentName;
        if (editing)
        {
            Title = "Change downloaded files";
            HeadingTextBlock.Text = "Change files to download";
            HintTextBlock.Text = "Deselected files stop downloading; already written data stays on disk.";
            AddSelectedButton.Content = "Apply selection";
        }
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
            var item = new TorrentFileSelectionItem(file);
            item.PropertyChanged += (_, _) => UpdateSelectionSummary();
            Files.Add(item);
        }

        TorrentNameTextBlock.Text = preview.Name;
        UpdateSelectionSummary();
        LoadingPanel.IsVisible = false;
        FilesListBox.IsVisible = true;
        SelectAllButton.IsEnabled = true;
        SelectNoneButton.IsEnabled = true;
        AddSelectedButton.IsEnabled = true;
        SequentialCheckBox.IsChecked = preview.Sequential;
        SequentialCheckBox.IsEnabled = true;
    }

    private void UpdateSelectionSummary()
    {
        var selectedCount = 0;
        long selectedBytes = 0;
        foreach (var file in Files)
        {
            if (file.IsSelected)
            {
                selectedCount++;
                selectedBytes += file.Length;
            }
        }

        SelectionSummaryTextBlock.Text = $"{selectedCount} / {Files.Count} selected · {Services.TransferFormatting.Bytes(selectedBytes)}";
    }

    public void UpdateLoadingStatus(string status, string? detail = null)
    {
        if (!IsVisible || !LoadingPanel.IsVisible)
        {
            return;
        }

        LoadingStatusTextBlock.Text = status;
        LoadingDetailTextBlock.Text = detail ?? string.Empty;
        LoadingDetailTextBlock.IsVisible = !string.IsNullOrWhiteSpace(detail);
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

        UpdateSelectionSummary();
    }

    private void SelectNone_Click(object? sender, RoutedEventArgs eventArgs)
    {
        foreach (var file in Files)
        {
            file.IsSelected = false;
        }

        UpdateSelectionSummary();
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

        Close(new FileSelection(selected, SequentialCheckBox.IsChecked == true));
    }
}
