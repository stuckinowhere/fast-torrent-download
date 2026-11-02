using System.IO.Pipes;

namespace FastTorrentDownload.Services;

/// <summary>
/// Hands magnet links and .torrent paths from a second launch (browser click, file
/// association) to the running instance over a local named pipe.
/// </summary>
public sealed class SingleInstanceChannel : IDisposable
{
    public const string DefaultPipeName = "WASD.FastTorrentDownload.sources";
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private Task? _listener;

    // Tests pass a unique name so a fixture never collides with an installed instance.
    public SingleInstanceChannel(string? pipeName = null) => _pipeName = pipeName ?? DefaultPipeName;

    public static IReadOnlyList<string> ExtractSources(IEnumerable<string> args) =>
        args.Select(arg => arg.Trim())
            .Where(arg => arg.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(Path.GetExtension(arg), ".torrent", StringComparison.OrdinalIgnoreCase))
            .ToArray();

    public static bool TryForward(IReadOnlyList<string> sources, string? pipeName = null)
    {
        if (sources.Count == 0)
        {
            return false;
        }

        try
        {
            using var client = new NamedPipeClientStream(".", pipeName ?? DefaultPipeName, PipeDirection.Out);
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
        _listener ??= Task.Run(() => ListenAsync(_pipeName, onSource, _stop.Token));
    }

    private static async Task ListenAsync(string pipeName, Action<string> onSource, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1,
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
