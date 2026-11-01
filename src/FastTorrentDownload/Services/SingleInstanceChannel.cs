using System.IO.Pipes;

namespace FastTorrentDownload.Services;

/// <summary>
/// Hands magnet links and .torrent paths from a second launch (browser click, file
/// association) to the running instance over a local named pipe.
/// </summary>
public sealed class SingleInstanceChannel : IDisposable
{
    private const string PipeName = "WASD.FastTorrentDownload.sources";
    private readonly CancellationTokenSource _stop = new();
    private Task? _listener;

    public static IReadOnlyList<string> ExtractSources(IEnumerable<string> args) =>
        args.Select(arg => arg.Trim())
            .Where(arg => arg.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(Path.GetExtension(arg), ".torrent", StringComparison.OrdinalIgnoreCase))
            .ToArray();

    public static bool TryForward(IReadOnlyList<string> sources)
    {
        if (sources.Count == 0)
        {
            return false;
        }

        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeout: 3000);
            using var writer = new StreamWriter(client);
            foreach (var source in sources)
            {
                writer.WriteLine(source);
            }

            writer.Flush();
            return true;
        }
        catch (Exception exception)
        {
            AppLogger.LogException("Could not forward sources to the running instance", exception);
            return false;
        }
    }

    public void Start(Action<string> onSource)
    {
        _listener ??= Task.Run(() => ListenAsync(onSource, _stop.Token));
    }

    private static async Task ListenAsync(Action<string> onSource, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellationToken);
                using var reader = new StreamReader(server);
                while (await reader.ReadLineAsync(cancellationToken) is { } line)
                {
                    foreach (var source in ExtractSources([line]))
                    {
                        onSource(source);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                AppLogger.LogException("Single-instance listener error", exception);
                try { await Task.Delay(500, cancellationToken); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}
