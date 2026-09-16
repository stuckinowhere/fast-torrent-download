namespace FastTorrentDownload.Models;

public sealed record TorrentFilePreview(string Path, long Length);

public sealed record TorrentAddPreview(
    string Id,
    string Name,
    IReadOnlyList<TorrentFilePreview> Files);
