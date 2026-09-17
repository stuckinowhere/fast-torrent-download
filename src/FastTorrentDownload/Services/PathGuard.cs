namespace FastTorrentDownload.Services;

public static class PathGuard
{
    public static string ResolveDestination(string configuredRoot, string? requestedDestination)
    {
        var candidate = string.IsNullOrWhiteSpace(requestedDestination) ? configuredRoot : requestedDestination;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            throw new ArgumentException("Choose a download folder.", nameof(requestedDestination));
        }

        var fullPath = Path.GetFullPath(candidate);
        Directory.CreateDirectory(fullPath);
        return fullPath;
    }

    public static string ResolveTorrentSource(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            throw new ArgumentException("Paste a magnet link or select a .torrent file.", nameof(source));
        }

        var trimmed = source.Trim();
        if (trimmed.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        var fullPath = Path.GetFullPath(source);
        if (!string.Equals(Path.GetExtension(fullPath), ".torrent", StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
        {
            throw new ArgumentException("The source must be a working magnet link or an existing .torrent file.", nameof(source));
        }

        return fullPath;
    }

}
