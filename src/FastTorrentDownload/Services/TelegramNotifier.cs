using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FastTorrentDownload.Services;

public sealed record TelegramResult(bool Ok, string? Error = null);

public sealed record TelegramChat(string Id, string Name);

/// <summary>
/// Sends short text alerts to the user's own Telegram bot through the official Bot API.
/// The bot token is part of every request URL, so URLs and raw exception text are never logged
/// or shown; failures are reported with fixed, translated sentences.
/// </summary>
public static partial class TelegramNotifier
{
    public const int MaximumTextLength = 1000;

    /// <summary>The Bot API host. Checks point it at a loopback server.</summary>
    public static Uri ApiBase { get; set; } = new("https://api.telegram.org");

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(8)
    })
    {
        Timeout = TimeSpan.FromSeconds(12)
    };

    [GeneratedRegex(@"^\d{5,12}:[A-Za-z0-9_-]{30,64}$")]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"^(-?\d{1,20}|@[A-Za-z][A-Za-z0-9_]{3,31})$")]
    private static partial Regex ChatPattern();

    public static bool IsValidToken(string? token) => token is not null && TokenPattern().IsMatch(token);

    /// <summary>A numeric chat id, or a public channel as @name.</summary>
    public static bool IsValidChat(string? chat) => chat is not null && ChatPattern().IsMatch(chat);

    public static async Task<TelegramResult> SendAsync(string? token, string? chat, string text, CancellationToken cancellationToken = default)
    {
        if (!IsValidToken(token))
        {
            return new(false, Loc.T("The bot token does not look right. Copy it again from @BotFather."));
        }

        if (!IsValidChat(chat))
        {
            return new(false, Loc.T("The chat is missing. Send a message to your bot, then press Detect chat."));
        }

        var body = new
        {
            chat_id = chat,
            text = text.Length > MaximumTextLength ? text[..MaximumTextLength] + "…" : text,
            // A torrent name can look like a web address; no preview is fetched for it.
            link_preview_options = new { is_disabled = true }
        };
        var (_, error) = await CallAsync(token!, "sendMessage", body, cancellationToken);
        return new(error is null, error);
    }

    /// <summary>Finds the chat of the most recent message sent to the bot.</summary>
    public static async Task<(TelegramChat? Chat, string? Error)> DetectChatAsync(string? token, CancellationToken cancellationToken = default)
    {
        if (!IsValidToken(token))
        {
            return (null, Loc.T("The bot token does not look right. Copy it again from @BotFather."));
        }

        var (result, error) = await CallAsync(token!, "getUpdates", new { limit = 50, timeout = 0, allowed_updates = new[] { "message" } }, cancellationToken);
        if (error is not null)
        {
            return (null, error);
        }

        TelegramChat? latest = null;
        if (result is { ValueKind: JsonValueKind.Array } updates)
        {
            foreach (var update in updates.EnumerateArray())
            {
                if (update.TryGetProperty("message", out var message) && message.TryGetProperty("chat", out var chat) &&
                    chat.TryGetProperty("id", out var id) && id.TryGetInt64(out var chatId))
                {
                    latest = new TelegramChat(chatId.ToString(System.Globalization.CultureInfo.InvariantCulture), DescribeChat(chat));
                }
            }
        }

        return latest is null
            ? (null, Loc.T("No message found. Open your bot in Telegram, send it any message, then press Detect chat again."))
            : (latest, null);
    }

    private static string DescribeChat(JsonElement chat)
    {
        static string? Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        var name = Text(chat, "title") ?? string.Join(' ', new[] { Text(chat, "first_name"), Text(chat, "last_name") }.Where(part => !string.IsNullOrWhiteSpace(part)));
        if (string.IsNullOrWhiteSpace(name))
        {
            name = Text(chat, "username") ?? string.Empty;
        }

        name = TorrentEngineService.SanitizeDisplayName(name);
        return name.Length > 60 ? name[..60] + "…" : name;
    }

    private static async Task<(JsonElement? Result, string? Error)> CallAsync(string token, string method, object body, CancellationToken cancellationToken)
    {
        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
            using var response = await Http.PostAsync(new Uri(ApiBase, $"/bot{token}/{method}"), content, cancellationToken);
            JsonElement? result = null;
            string? description = null;
            var ok = false;
            try
            {
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                var root = document.RootElement;
                ok = root.TryGetProperty("ok", out var flag) && flag.ValueKind == JsonValueKind.True;
                if (root.TryGetProperty("result", out var value)) result = value.Clone();
                if (root.TryGetProperty("description", out var text) && text.ValueKind == JsonValueKind.String) description = text.GetString();
            }
            catch (JsonException)
            {
                // Not a Bot API answer (a proxy page, for example); the status code decides below.
            }

            if (ok && response.IsSuccessStatusCode)
            {
                return (result, null);
            }

            AppLogger.Log($"Telegram {method} failed: HTTP {(int)response.StatusCode}.");
            return (null, Explain(response.StatusCode, description));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or IOException)
        {
            // Only the type: the message of a transport error may quote the request address.
            AppLogger.Log($"Telegram {method} failed: {exception.GetType().Name}.");
            return (null, Loc.T("Could not reach Telegram. Check the internet connection and try again."));
        }
    }

    private static string Explain(HttpStatusCode status, string? description) => (int)status switch
    {
        401 or 404 => Loc.T("Telegram rejected the bot token. Copy it again from @BotFather."),
        400 when description?.Contains("chat not found", StringComparison.OrdinalIgnoreCase) == true =>
            Loc.T("Telegram cannot find that chat. Send a message to your bot, then press Detect chat."),
        403 => Loc.T("The bot is not allowed to write to that chat. Open the bot in Telegram and press Start."),
        409 => Loc.T("Another program is reading this bot's messages. Type the chat id by hand, or use a bot of its own."),
        429 => Loc.T("Telegram is limiting requests. Try again in a minute."),
        _ => Loc.I($"Telegram answered with error {(int)status}.")
    };
}
