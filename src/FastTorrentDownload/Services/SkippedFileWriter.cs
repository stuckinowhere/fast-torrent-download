using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using MonoTorrent;
using MonoTorrent.PieceWriter;
using ReusableTasks;

namespace FastTorrentDownload.Services;

/// <summary>
/// Keeps files the user did not select out of the download folder.
/// <para>
/// A piece often spans two files. When one of them is selected, BitTorrent can only verify the piece
/// by downloading all of it, so a few bytes of the unselected neighbour arrive too. MonoTorrent writes
/// those bytes into the neighbour's own file, which made deselected files appear in the folder (small
/// ones in full). This writer sends the data of a deselected file to a hidden file in the app cache
/// instead. A deselected file that already exists in the folder is left alone and used in place, so
/// nothing the user owns is moved, hidden or deleted.
/// </para>
/// <para>
/// When such a file is selected later, <see cref="Reveal(ITorrentManagerFile)"/> moves the hidden file
/// into the folder: it already holds the bytes downloaded so far at the right offsets. Revealing is
/// always explicit. The priority seen during a read or write cannot decide it, because the engine
/// touches every file while restoring a saved queue, before it has restored the priorities.
/// </para>
/// </summary>
public sealed class SkippedFileWriter(IPieceWriter inner, string skippedDirectory) : IPieceWriter
{
    private sealed class Entry(ITorrentManagerFile file, string skippedDirectory)
    {
        private volatile bool _hasHidden = File.Exists(HiddenPath(skippedDirectory, file.FullPath));

        public SkippedFile Hidden { get; } = new(file, skippedDirectory);
        /// <summary>A hidden file exists for this torrent file. Only this writer creates or removes it.</summary>
        public bool HasHidden { get => _hasHidden; set => _hasHidden = value; }
    }

    // Keyed by the torrent's own file objects, which live exactly as long as the torrent is in the queue.
    private readonly ConditionalWeakTable<ITorrentManagerFile, Entry> _entries = new();

    public int OpenFiles => inner.OpenFiles;
    public int MaximumOpenFiles => inner.MaximumOpenFiles;

    /// <summary>Where the data of a deselected file is kept, derived from its place in the download folder.</summary>
    public static string HiddenPath(string skippedDirectory, string fullPath)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(fullPath).ToUpperInvariant()));
        return Path.Combine(skippedDirectory, Convert.ToHexString(digest, 0, 20) + ".part");
    }

    /// <summary>Removes the hidden files of a torrent that left the queue. Call once the torrent is stopped.</summary>
    public static void DeleteHidden(string skippedDirectory, IEnumerable<string> fullPaths)
    {
        foreach (var fullPath in fullPaths)
        {
            try { File.Delete(HiddenPath(skippedDirectory, fullPath)); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Removes hidden files that no queued torrent refers to (left by a crash, for example).</summary>
    public static void DeleteOrphans(string skippedDirectory, IEnumerable<string> queuedFullPaths)
    {
        if (!Directory.Exists(skippedDirectory))
        {
            return;
        }

        var wanted = queuedFullPaths.Select(path => HiddenPath(skippedDirectory, path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(skippedDirectory, "*.part"))
        {
            if (wanted.Contains(file)) continue;
            try { File.Delete(file); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Moves the hidden data of a file into the download folder and sends its reads and writes there
    /// from now on. Call right after the file is selected: a small file may already be complete inside
    /// a shared piece, and then no later read or write would bring it out.
    /// </summary>
    public void Reveal(ITorrentManagerFile file)
    {
        var entry = _entries.GetValue(file, key => new Entry(key, skippedDirectory));
        Reveal(skippedDirectory, file.FullPath);
        // Handles already open on the hidden file follow the rename (they share delete access) and stay valid.
        entry.HasHidden = false;
    }

    /// <summary>The file move behind <see cref="Reveal(ITorrentManagerFile)"/>, by path.</summary>
    public static void Reveal(string skippedDirectory, string fullPath)
    {
        var hidden = HiddenPath(skippedDirectory, fullPath);
        try
        {
            if (!File.Exists(hidden))
            {
                return;
            }

            if (File.Exists(fullPath))
            {
                File.Delete(hidden);
                return;
            }

            if (Path.GetDirectoryName(fullPath) is { Length: > 0 } folder)
            {
                Directory.CreateDirectory(folder);
            }

            File.Move(hidden, fullPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The engine then creates the file itself; a recheck re-downloads the few bytes left behind.
            AppLogger.Log($"Could not move a previously skipped file into the download folder ({exception.GetType().Name}).");
        }
    }

    // The file MonoTorrent's request is carried out on: the torrent's own file, or its hidden stand-in.
    private ITorrentManagerFile Resolve(ITorrentManagerFile file, out Entry entry)
    {
        entry = _entries.GetValue(file, key => new Entry(key, skippedDirectory));
        // Hidden data stays hidden until Reveal, whatever the priority reads at this moment.
        if (entry.HasHidden)
        {
            return entry.Hidden;
        }

        // A deselected file that is not in the folder yet is kept out; one that is already there is used in place.
        return file.Priority == Priority.DoNotDownload && !File.Exists(file.FullPath) ? entry.Hidden : file;
    }

    public async ReusableTask CloseAsync(ITorrentManagerFile file)
    {
        // Both calls start on the caller's context, which the disk writer requires.
        var closeFile = inner.CloseAsync(file);
        if (_entries.TryGetValue(file, out var entry))
        {
            var closeHidden = inner.CloseAsync(entry.Hidden);
            await closeFile;
            await closeHidden;
            return;
        }

        await closeFile;
    }

    public ReusableTask<bool> CreateAsync(ITorrentManagerFile file, FileCreationOptions options)
    {
        var target = Resolve(file, out var entry);
        if (ReferenceEquals(target, entry.Hidden))
        {
            entry.HasHidden = true;
        }

        return inner.CreateAsync(target, options);
    }

    public ReusableTask<bool> ExistsAsync(ITorrentManagerFile file) => inner.ExistsAsync(Resolve(file, out _));

    public ReusableTask FlushAsync(ITorrentManagerFile file) => inner.FlushAsync(Resolve(file, out _));

    public ReusableTask<long?> GetLengthAsync(ITorrentManagerFile file) => inner.GetLengthAsync(Resolve(file, out _));

    public ReusableTask MoveAsync(ITorrentManagerFile file, string fullPath, bool overwrite)
    {
        var target = Resolve(file, out var entry);
        if (!ReferenceEquals(target, entry.Hidden))
        {
            return inner.MoveAsync(file, fullPath, overwrite);
        }

        // The hidden file is named after the file's place in the download folder, so it follows the move.
        return inner.MoveAsync(entry.Hidden, HiddenPath(skippedDirectory, fullPath), overwrite: true);
    }

    public ReusableTask<int> ReadAsync(ITorrentManagerFile file, long offset, Memory<byte> buffer)
    {
        var target = Resolve(file, out var entry);
        // Nothing was ever written for this file: report "no data" without creating an empty hidden file.
        return ReferenceEquals(target, entry.Hidden) && !entry.HasHidden
            ? ReusableTask.FromResult(0)
            : inner.ReadAsync(target, offset, buffer);
    }

    public ReusableTask<bool> SetLengthAsync(ITorrentManagerFile file, long length) => inner.SetLengthAsync(Resolve(file, out _), length);

    public ReusableTask SetMaximumOpenFilesAsync(int maximumOpenFiles) => inner.SetMaximumOpenFilesAsync(maximumOpenFiles);

    public ReusableTask WriteAsync(ITorrentManagerFile file, long offset, ReadOnlyMemory<byte> buffer)
    {
        var target = Resolve(file, out var entry);
        if (ReferenceEquals(target, entry.Hidden))
        {
            entry.HasHidden = true;
        }

        return inner.WriteAsync(target, offset, buffer);
    }

    public void Dispose() => inner.Dispose();

    /// <summary>The same torrent file, stored at its hidden path.</summary>
    private sealed class SkippedFile(ITorrentManagerFile file, string skippedDirectory) : ITorrentManagerFile
    {
        private string? _source;
        private string? _hidden;

        public string FullPath
        {
            get
            {
                // The download folder can change (Move…); the hidden name follows it.
                var source = file.FullPath;
                if (!ReferenceEquals(source, _source))
                {
                    _hidden = HiddenPath(skippedDirectory, source);
                    _source = source;
                }

                return _hidden!;
            }
        }

        public string DownloadCompleteFullPath => FullPath;
        public string DownloadIncompleteFullPath => FullPath;
        public ReadOnlyBitField BitField => file.BitField;
        public Priority Priority => file.Priority;
        public string Path => file.Path;
        public int StartPieceIndex => file.StartPieceIndex;
        public int EndPieceIndex => file.EndPieceIndex;
        public int PieceCount => file.PieceCount;
        public long Length => file.Length;
        public long Padding => file.Padding;
        public long OffsetInTorrent => file.OffsetInTorrent;
        public MerkleRoot PiecesRoot => file.PiecesRoot;
    }
}
