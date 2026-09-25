using CommunityToolkit.Mvvm.ComponentModel;
using FastTorrentDownload.Models;

namespace FastTorrentDownload.ViewModels;

public sealed partial class TorrentRowViewModel(TorrentSnapshot snapshot) : ObservableObject
{
    public string Id { get; } = snapshot.Id;
    [ObservableProperty] private string _name = snapshot.Name;
    [ObservableProperty] private string _destination = snapshot.Destination;
    [ObservableProperty] private string _state = snapshot.State;
    [ObservableProperty] private double _progress = snapshot.Progress;
    [ObservableProperty] private long _downloadRate = snapshot.DownloadRate;
    [ObservableProperty] private long _uploadRate = snapshot.UploadRate;
    [ObservableProperty] private long _downloadedBytes = snapshot.DownloadedBytes;
    [ObservableProperty] private long _uploadedBytes = snapshot.UploadedBytes;
    [ObservableProperty] private int _seederCount = snapshot.SeederCount;
    [ObservableProperty] private int _leecherCount = snapshot.LeecherCount;

    public string ProgressLabel => $"{Progress:0.0}%";
    public string TransferLabel => $"↓ {FormatRate(DownloadRate)}   ↑ {FormatRate(UploadRate)}";
    public string DetailLabel => $"{FormatBytes(DownloadedBytes)} received · ratio {Ratio:0.00} · {SeederCount} seeders · {LeecherCount} peers";
    public double Ratio => DownloadedBytes == 0 ? 0 : (double)UploadedBytes / DownloadedBytes;

    public void Update(TorrentSnapshot snapshot)
    {
        Name = snapshot.Name;
        Destination = snapshot.Destination;
        State = snapshot.State;
        Progress = snapshot.Progress;
        DownloadRate = snapshot.DownloadRate;
        UploadRate = snapshot.UploadRate;
        DownloadedBytes = snapshot.DownloadedBytes;
        UploadedBytes = snapshot.UploadedBytes;
        SeederCount = snapshot.SeederCount;
        LeecherCount = snapshot.LeecherCount;
        NotifyDerivedProperties();
    }

    partial void OnProgressChanged(double value) => NotifyDerivedProperties();
    partial void OnDownloadRateChanged(long value) => OnPropertyChanged(nameof(TransferLabel));
    partial void OnUploadRateChanged(long value) => OnPropertyChanged(nameof(TransferLabel));
    partial void OnDownloadedBytesChanged(long value) => NotifyDerivedProperties();
    partial void OnUploadedBytesChanged(long value) => NotifyDerivedProperties();
    partial void OnSeederCountChanged(int value) => NotifyDerivedProperties();
    partial void OnLeecherCountChanged(int value) => NotifyDerivedProperties();
    private void NotifyDerivedProperties()
    {
        OnPropertyChanged(nameof(ProgressLabel));
        OnPropertyChanged(nameof(TransferLabel));
        OnPropertyChanged(nameof(DetailLabel));
        OnPropertyChanged(nameof(Ratio));
    }

    public static string FormatRate(long bytesPerSecond) => $"{FormatBytes(bytesPerSecond)}/s";

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value} {units[unit]}" : $"{value:0.0} {units[unit]}";
    }
}
