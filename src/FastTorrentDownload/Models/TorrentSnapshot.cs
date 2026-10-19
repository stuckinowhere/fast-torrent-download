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
    int SeederCount,
    int LeecherCount,
    string? Operation = null,
    long SelectedBytes = 0,
    long VerifiedBytes = 0,
    TorrentDiagnostics? Diagnostics = null)
{
    public double Ratio => DownloadedBytes == 0 ? 0 : (double)UploadedBytes / DownloadedBytes;
}
