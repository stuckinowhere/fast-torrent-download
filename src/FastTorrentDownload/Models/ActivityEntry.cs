namespace FastTorrentDownload.Models;

public sealed record ActivityEntry(DateTimeOffset Time, string Message, string? Destination = null)
{
    public string Label => $"{Time:HH:mm:ss} · {Message}";
    public bool CanOpenFolder => Destination is not null;
}
