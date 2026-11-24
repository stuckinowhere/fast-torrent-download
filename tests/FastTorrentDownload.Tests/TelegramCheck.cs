using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FastTorrentDownload.Models;
using FastTorrentDownload.Services;

/// <summary>A loopback stand-in for the Telegram Bot API: records each request and answers from a script.</summary>
internal sealed class FakeTelegram : IDisposable
{
    public const string Token = "123456789:AAFakeTokenForChecksOnly_0123456789abcd";
    public const string Chat = "424242";
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();

    public ConcurrentQueue<(string Path, string Body)> Requests { get; } = new();
    public Func<string, string, (int Status, string Json)> Answer { get; set; } = (_, _) => (200, """{"ok":true,"result":{}}""");
    public Uri Base { get; }

    public FakeTelegram()
    {
        _listener.Start();
        Base = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}");
        _ = ServeAsync();
    }

    /// <summary>The texts of every sendMessage call, in arrival order.</summary>
    public string[] SentTexts() => Requests.Where(request => request.Path.EndsWith("/sendMessage", StringComparison.Ordinal))
        .Select(request => JsonDocument.Parse(request.Body).RootElement.GetProperty("text").GetString()!).ToArray();

    private async Task ServeAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                var stream = client.GetStream();
                var received = new List<byte>();
                var buffer = new byte[8192];
                int headerEnd;
                while ((headerEnd = IndexOf(received, "\r\n\r\n"u8)) < 0)
                {
                    var read = await stream.ReadAsync(buffer, _stop.Token);
                    if (read == 0) break;
                    received.AddRange(buffer.AsSpan(0, read));
                }

                if (headerEnd < 0) continue;
                var head = Encoding.ASCII.GetString(received.ToArray(), 0, headerEnd);
                var lines = head.Split("\r\n");
                var length = lines.Select(line => line.Split(':', 2)).Where(parts => parts.Length == 2 && parts[0].Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    .Select(parts => int.Parse(parts[1].Trim())).FirstOrDefault();
                while (received.Count < headerEnd + 4 + length)
                {
                    var read = await stream.ReadAsync(buffer, _stop.Token);
                    if (read == 0) break;
                    received.AddRange(buffer.AsSpan(0, read));
                }

                var path = lines[0].Split(' ')[1];
                var body = Encoding.UTF8.GetString(received.ToArray(), headerEnd + 4, Math.Min(length, received.Count - headerEnd - 4));
                Requests.Enqueue((path, body));
                var (status, json) = Answer(path, body);
                var payload = Encoding.UTF8.GetBytes(json);
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} X\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n"), _stop.Token);
                await stream.WriteAsync(payload, _stop.Token);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or SocketException or IOException or ObjectDisposedException)
        {
        }
    }

    private static int IndexOf(List<byte> data, ReadOnlySpan<byte> pattern)
    {
        for (var index = 0; index <= data.Count - pattern.Length; index++)
        {
            var match = true;
            for (var offset = 0; offset < pattern.Length && match; offset++) match = data[index + offset] == pattern[offset];
            if (match) return index;
        }

        return -1;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }
}

internal static class TelegramCheck
{
    public static async Task RunAsync()
    {
        var original = TelegramNotifier.ApiBase;
        var root = Path.Combine(Path.GetTempPath(), "fast-torrent-telegram-" + Guid.NewGuid().ToString("N"));
        using var server = new FakeTelegram();
        try
        {
            TelegramNotifier.ApiBase = server.Base;

            // The token is encrypted at rest and never reaches settings.json in plain text.
            var sealedToken = SecretProtector.Protect(FakeTelegram.Token);
            Require(sealedToken.StartsWith("dpapi:", StringComparison.Ordinal) && !sealedToken.Contains("AAFakeToken") && SecretProtector.Unprotect(sealedToken) == FakeTelegram.Token,
                "The token must round-trip through DPAPI without appearing in the stored value.");
            Require(SecretProtector.Protect("") == "" && SecretProtector.Unprotect("") is null && SecretProtector.Unprotect("dpapi:not-base64!") is null &&
                    SecretProtector.Unprotect("dpapi:" + Convert.ToBase64String(new byte[40])) is null && SecretProtector.Unprotect(FakeTelegram.Token) is null,
                "Empty, malformed and foreign values must read as no secret.");
            var paths = new AppPaths(root);
            paths.EnsureDirectories();
            AppLogger.Init(paths);
            var store = new SettingsStore(paths);
            await store.SaveAsync(new AppSettings { TelegramEnabled = true, TelegramBotToken = sealedToken, TelegramChatId = " 424242 " });
            var saved = await File.ReadAllTextAsync(paths.SettingsFile);
            var loaded = await store.LoadAsync();
            Require(!saved.Contains("AAFakeToken") && loaded.TelegramEnabled && loaded.TelegramChatId == "424242" && loaded.TelegramNotifyProblems &&
                    SecretProtector.Unprotect(loaded.TelegramBotToken) == FakeTelegram.Token, "Telegram settings must persist with the token encrypted.");
            Require(!new AppSettings().TelegramEnabled, "Phone alerts must be off by default.");

            Require(TelegramNotifier.IsValidToken(FakeTelegram.Token) && !TelegramNotifier.IsValidToken("123:short") && !TelegramNotifier.IsValidToken(null) &&
                    !TelegramNotifier.IsValidToken(FakeTelegram.Token + "/../x"), "Token validation is wrong.");
            Require(TelegramNotifier.IsValidChat("424242") && TelegramNotifier.IsValidChat("-1001234567890") && TelegramNotifier.IsValidChat("@my_channel") &&
                    !TelegramNotifier.IsValidChat("") && !TelegramNotifier.IsValidChat("me") && !TelegramNotifier.IsValidChat("12 34"), "Chat validation is wrong.");

            // A good send: one POST to the bot's own sendMessage with the chat and text, and no link preview.
            var sent = await TelegramNotifier.SendAsync(FakeTelegram.Token, FakeTelegram.Chat, "Download complete: example.mkv");
            Require(sent.Ok && server.Requests.Count == 1, "A good send must make exactly one request.");
            server.Requests.TryDequeue(out var request);
            Require(request.Path == $"/bot{FakeTelegram.Token}/sendMessage", "The alert was not posted to sendMessage.");
            var json = JsonDocument.Parse(request.Body).RootElement;
            Require(json.GetProperty("chat_id").GetString() == FakeTelegram.Chat && json.GetProperty("text").GetString() == "Download complete: example.mkv" &&
                    json.GetProperty("link_preview_options").GetProperty("is_disabled").GetBoolean(), "The alert body is wrong.");
            var longText = await TelegramNotifier.SendAsync(FakeTelegram.Token, FakeTelegram.Chat, new string('n', 5000));
            Require(longText.Ok && server.SentTexts().Single().Length == TelegramNotifier.MaximumTextLength + 1, "A long name must be cut before sending.");
            server.Requests.Clear();

            // Nothing leaves the PC for an invalid token or chat.
            var badToken = await TelegramNotifier.SendAsync("nonsense", FakeTelegram.Chat, "x");
            var badChat = await TelegramNotifier.SendAsync(FakeTelegram.Token, "", "x");
            Require(!badToken.Ok && !badChat.Ok && server.Requests.IsEmpty, "Invalid settings must not produce a request.");

            // Failures name the fix and never echo the token.
            server.Answer = (_, _) => (401, """{"ok":false,"error_code":401,"description":"Unauthorized"}""");
            var rejected = await TelegramNotifier.SendAsync(FakeTelegram.Token, FakeTelegram.Chat, "x");
            Require(!rejected.Ok && rejected.Error == "Telegram rejected the bot token. Copy it again from @BotFather.", $"Unexpected 401 text: {rejected.Error}");
            server.Answer = (_, _) => (400, """{"ok":false,"error_code":400,"description":"Bad Request: chat not found"}""");
            var noChat = await TelegramNotifier.SendAsync(FakeTelegram.Token, FakeTelegram.Chat, "x");
            Require(noChat.Error == "Telegram cannot find that chat. Send a message to your bot, then press Detect chat.", $"Unexpected 400 text: {noChat.Error}");
            server.Answer = (_, _) => (502, "<html>bad gateway</html>");
            var gateway = await TelegramNotifier.SendAsync(FakeTelegram.Token, FakeTelegram.Chat, "x");
            Require(gateway.Error == "Telegram answered with error 502.", $"Unexpected 502 text: {gateway.Error}");

            // Detect chat: the most recent message decides; no messages explains what to do.
            server.Answer = (_, _) => (200, """{"ok":true,"result":[{"update_id":1,"message":{"chat":{"id":111,"type":"private","first_name":"Old"}}},{"update_id":2,"edited_message":{}},{"update_id":3,"message":{"chat":{"id":424242,"type":"private","first_name":"Ada","last_name":"L"}}}]}""");
            var (chat, detectError) = await TelegramNotifier.DetectChatAsync(FakeTelegram.Token);
            Require(detectError is null && chat == new TelegramChat("424242", "Ada L"), $"Detect chat returned {chat} / {detectError}.");
            server.Answer = (_, _) => (200, """{"ok":true,"result":[]}""");
            var (none, noneError) = await TelegramNotifier.DetectChatAsync(FakeTelegram.Token);
            Require(none is null && noneError!.StartsWith("No message found.", StringComparison.Ordinal), "An empty inbox must explain the next step.");

            // An unreachable server is reported, not thrown, and the token stays out of the message and the log.
            server.Dispose();
            var offline = await TelegramNotifier.SendAsync(FakeTelegram.Token, FakeTelegram.Chat, "x");
            Require(!offline.Ok && offline.Error == "Could not reach Telegram. Check the internet connection and try again.", $"Unexpected offline text: {offline.Error}");
            foreach (var text in new[] { rejected.Error, noChat.Error, gateway.Error, offline.Error, noneError })
            {
                Require(!text!.Contains("AAFakeToken") && !text.Contains("/bot"), "An error text leaked the token or the request address.");
            }

            var log = await File.ReadAllTextAsync(Path.Combine(paths.LogsDirectory, "app.log"));
            Require(log.Contains("Telegram sendMessage failed: HTTP 401.") && log.Contains("Telegram sendMessage failed: HttpRequestException."), "Failures must be logged by kind.");
            Require(!log.Contains("AAFakeToken") && !log.Contains("/bot"), "The log leaked the bot token or the request address.");

            Console.WriteLine("PASS Telegram alerts: token encrypted at rest, validated before any request, sent to the bot's own chat, failures explained without leaking the token, chat detected from the latest message");
        }
        finally
        {
            TelegramNotifier.ApiBase = original;
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
