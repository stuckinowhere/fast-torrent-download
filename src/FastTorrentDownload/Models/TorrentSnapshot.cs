namespace FastTorrentDownload.Models;

public sealed record TorrentSnapshot(
    string Id,
    string Name,
    string Destination,
    string State,
    double Progress,
    long DownloadRate,
    long UploadRate,
    long DownloadedBytes,
    long UploadedBytes,
    bool IsComplete)
{
    public double Ratio => DownloadedBytes == 0 ? 0 : (double)UploadedBytes / DownloadedBytes;
}
