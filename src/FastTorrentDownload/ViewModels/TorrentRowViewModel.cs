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
    private long _downloadRate = snapshot.DownloadRate;
    private long _uploadRate = snapshot.UploadRate;
    private long _downloadedBytes = snapshot.DownloadedBytes;
    private long _uploadedBytes = snapshot.UploadedBytes;
    private int _seederCount = snapshot.SeederCount;
    private int _leecherCount = snapshot.LeecherCount;

    public string ProgressLabel => $"{Progress:0.0}%";
    public string TransferLabel => $"↓ {FormatRate(_downloadRate)}   ↑ {FormatRate(_uploadRate)}";
    public string DetailLabel => $"{FormatBytes(_downloadedBytes)} received · ratio {Ratio:0.00} · {_seederCount} seeders · {_leecherCount} peers";
    private double Ratio => _downloadedBytes == 0 ? 0 : (double)_uploadedBytes / _downloadedBytes;

    public void Update(TorrentSnapshot snapshot)
    {
        Name = snapshot.Name;
        Destination = snapshot.Destination;
        State = snapshot.State;
        Progress = snapshot.Progress;
        _downloadRate = snapshot.DownloadRate;
        _uploadRate = snapshot.UploadRate;
        _downloadedBytes = snapshot.DownloadedBytes;
        _uploadedBytes = snapshot.UploadedBytes;
        _seederCount = snapshot.SeederCount;
        _leecherCount = snapshot.LeecherCount;
        OnPropertyChanged(nameof(TransferLabel));
        OnPropertyChanged(nameof(DetailLabel));
    }

    partial void OnProgressChanged(double value) => OnPropertyChanged(nameof(ProgressLabel));

    public static string FormatRate(long bytesPerSecond) => $"{FormatBytes(bytesPerSecond)}/s";

    private static string FormatBytes(long bytes)
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
