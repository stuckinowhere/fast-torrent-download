using System.Net;
using System.Text.Json;

namespace FastTorrentDownload.Services;

public sealed record AvailableUpdate(Version Version, string Tag, Uri ReleasePage);

public sealed class ReleaseUpdateService
{
    private static readonly HttpClient Client = CreateClient();

    public async Task<AvailableUpdate?> CheckAsync(string repository, Version installedVersion, CancellationToken cancellationToken = default)
    {
        if (!IsRepositoryName(repository))
        {
            return null;
        }

        using var response = await Client.GetAsync($"repos/{repository}/releases/latest", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
        var page = root.TryGetProperty("html_url", out var pageElement) ? pageElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(tag) || !Version.TryParse(tag.TrimStart('v', 'V'), out var version) ||
            !Uri.TryCreate(page, UriKind.Absolute, out var releasePage) ||
            !string.Equals(releasePage.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(releasePage.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            version <= installedVersion)
        {
            return null;
        }

        return new AvailableUpdate(version, tag, releasePage);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri("https://api.github.com/"),
            Timeout = TimeSpan.FromSeconds(10)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("fast-torrent-download/0.1");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static bool IsRepositoryName(string repository)
    {
        var parts = repository.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 && parts.All(part => part.All(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_' or '.'));
    }
}
