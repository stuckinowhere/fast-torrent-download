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

    /// <summary>
    /// MonoTorrent 3.0.2 PathValidator misses Windows-rooted <c>\foo</c>, mixed-separator
    /// <c>..</c> walks, and the entire v2 file tree. Combined with Path.Combine dropping
    /// the destination when the torrent path is rooted, that writes outside the download folder.
    /// </summary>
    public static bool IsSafeTorrentFilePath(string destinationRoot, string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(destinationRoot))
        {
            return false;
        }

        var trimmed = filePath.Trim();
        if (trimmed.Contains('\0'))
        {
            return false;
        }

        // Rooted / UNC / drive-absolute on any OS, including Windows-style paths in Linux tests.
        if (Path.IsPathRooted(trimmed) ||
            trimmed[0] is '/' or '\\' ||
            (trimmed.Length >= 2 && trimmed[1] == ':'))
        {
            return false;
        }

        var parts = trimmed.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(part => part is "." or ".." || part.Contains(':')))
        {
            return false;
        }

        try
        {
            var dest = Path.GetFullPath(destinationRoot);
            var destPrefix = dest.EndsWith(Path.DirectorySeparatorChar) || dest.EndsWith(Path.AltDirectorySeparatorChar)
                ? dest
                : dest + Path.DirectorySeparatorChar;
            var combined = Path.GetFullPath(Path.Combine(dest, trimmed));
            return combined.StartsWith(destPrefix, StringComparison.OrdinalIgnoreCase)
                || string.Equals(combined, dest, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public static void ThrowIfUnsafeTorrentFiles(string destinationRoot, IEnumerable<string> filePaths)
    {
        foreach (var filePath in filePaths)
        {
            if (!IsSafeTorrentFilePath(destinationRoot, filePath))
            {
                throw new InvalidDataException("This torrent contains a file path that would write outside the download folder.");
            }
        }
    }
}
