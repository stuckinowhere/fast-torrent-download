namespace FastTorrentDownload.Models;

public sealed record TorrentFilePreview(string Path, long Length, bool Selected = true);

public sealed record TorrentAddPreview(
    string Id,
    string Name,
    IReadOnlyList<TorrentFilePreview> Files);
