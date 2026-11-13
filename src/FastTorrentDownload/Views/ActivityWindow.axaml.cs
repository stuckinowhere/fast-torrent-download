using FastTorrentDownload.Services;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using FastTorrentDownload.Models;

namespace FastTorrentDownload.Views;

public partial class ActivityWindow : Window
{
    public ObservableCollection<ActivityEntry> Activity { get; }

    public ActivityWindow(ObservableCollection<ActivityEntry> activity)
    {
        Activity = activity;
        DataContext = this;
        InitializeComponent();
        Loc.Apply(this);
    }

    private void OpenFolder_Click(object? sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: string folder }) return;
        try
        {
            if (Directory.Exists(folder)) Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
            else ((Button)sender).Content = Loc.T("Folder unavailable");
        }
        catch (Exception exception)
        {
            Services.AppLogger.LogException("Open activity folder failed", exception);
            ((Button)sender).Content = Loc.T("Could not open folder");
        }
    }

    private void Row_Loaded(object? sender, RoutedEventArgs args)
    {
        if (sender is Control row)
        {
            Loc.Apply(row);
        }
    }

    private void Close_Click(object? sender, RoutedEventArgs args) => Close();
}
