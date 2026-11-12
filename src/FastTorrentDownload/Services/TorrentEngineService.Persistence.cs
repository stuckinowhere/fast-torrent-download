using System.Net;
using System.Collections.Concurrent;
using FastTorrentDownload.Models;
using MonoTorrent;
using MonoTorrent.BEncoding;
using MonoTorrent.Client;
using MonoTorrent.Connections;
using MonoTorrent.Trackers;

namespace FastTorrentDownload.Services;

// Saving the committed queue and deciding which state-file failures are recoverable.
public sealed partial class TorrentEngineService
{
    // Caller holds _gate so queue changes cannot race the snapshot or file replacement.
    private async Task SaveStateAsync(string? excludedId = null)
    {
        byte[] buffer;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                buffer = await RequireEngine().SaveStateAsync();
                break;
            }
            catch (IOException exception) when ((exception.HResult & 0xffff) is 32 or 33 && attempt < 4)
            {
                // MonoTorrent can still be writing fast-resume data after a state transition.
                await Task.Delay(100);
            }
        }
        var state = BEncodedValue.Decode<BEncodedDictionary>(buffer);
        var committed = _entries.Where(entry => entry.Key != excludedId).Select(entry => entry.Value.Manager.MagnetLink.ToV1String()).ToHashSet();
        var torrents = (BEncodedList)state[nameof(ClientEngine.Torrents)];
        for (var index = torrents.Count - 1; index >= 0; index--)
        {
            if (!committed.Contains(((BEncodedString)((BEncodedDictionary)torrents[index])[nameof(TorrentManager.MagnetLink)]).Text))
            {
                torrents.RemoveAt(index);
            }
        }

        var excludedHash = excludedId is not null && _entries.TryGetValue(excludedId, out var excluded)
            ? DescribeInfoHashes(excluded.Manager.InfoHashes) : null;
        await WriteStateAsync(state, excludedHash);
    }

    private async Task WriteStateAsync(BEncodedDictionary state, string? excludedHash = null)
    {
        state["PausedInfoHashes"] = new BEncodedList(_pausedHashes.Where(hash => hash != excludedHash).Select(hash => (BEncodedValue)new BEncodedString(hash)));
        var committed = _entries.Values.Select(entry => DescribeInfoHashes(entry.Manager.InfoHashes)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var extraTrackers = new BEncodedDictionary();
        foreach (var pair in _extraTrackers.Where(pair => pair.Key != excludedHash && committed.Contains(pair.Key)))
        {
            extraTrackers[pair.Key] = new BEncodedList(pair.Value.Select(url => (BEncodedValue)new BEncodedString(url)));
        }

        state["ExtraTrackers"] = extraTrackers;
        var temporaryFile = $"{_paths.EngineStateFile}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporaryFile, state.Encode());
            File.Move(temporaryFile, _paths.EngineStateFile, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryFile))
            {
                File.Delete(temporaryFile);
            }
        }
    }

    private static bool IsRecoverableStateException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException
            or FormatException or InvalidCastException or KeyNotFoundException or NullReferenceException
            or IndexOutOfRangeException or OverflowException or System.Text.Json.JsonException
            or BEncodingException or TorrentException;
}
