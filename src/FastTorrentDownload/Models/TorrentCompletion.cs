namespace FastTorrentDownload.Models;

public sealed record TorrentCompletion(string Name, string Destination, string? Error = null);
