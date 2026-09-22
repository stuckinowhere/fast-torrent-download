using System.Net;
using System.Text.Json;

namespace FastTorrentDownload.Services;

public sealed record AvailableUpdate(string Tag, Uri PackageUrl, Uri ChecksumUrl);

public sealed record ReleaseAsset(string Name, Uri DownloadUrl);

public sealed class ReleaseUpdateService
{
    public const string Repository = "stuckinowhere/fast-torrent-download";

    private static readonly HttpClient Client = CreateClient();

    public async Task<AvailableUpdate?> CheckAsync(Version installedVersion, CancellationToken cancellationToken = default)
    {
        using var response = await Client.GetAsync($"repos/{Repository}/releases/latest", cancellationToken);
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

        var assets = ReadAssets(root);
        var package = SelectPackage(assets);
        if (package is null)
        {
            return null;
        }

        return new AvailableUpdate(tag, package.Value.PackageUrl, package.Value.ChecksumUrl);
    }

    private static List<ReleaseAsset> ReadAssets(JsonElement root)
    {
        var assets = new List<ReleaseAsset>();
        if (!root.TryGetProperty("assets", out var assetsElement) || assetsElement.ValueKind != JsonValueKind.Array)
        {
            return assets;
        }

        foreach (var asset in assetsElement.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
            var url = asset.TryGetProperty("browser_download_url", out var urlElement) ? urlElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(name) || !Uri.TryCreate(url, UriKind.Absolute, out var downloadUrl))
            {
                continue;
            }

            assets.Add(new ReleaseAsset(name, downloadUrl));
        }

        return assets;
    }

    public static (Uri PackageUrl, Uri ChecksumUrl)? SelectPackage(IReadOnlyList<ReleaseAsset> assets)
    {
        var package = assets.FirstOrDefault(asset =>
            asset.Name.EndsWith("-portable.zip", StringComparison.OrdinalIgnoreCase) &&
            IsDownloadHost(asset.DownloadUrl));
        if (package is null)
        {
            return null;
        }

        var checksum = assets.FirstOrDefault(asset =>
            asset.Name.Equals(package.Name + ".sha256", StringComparison.OrdinalIgnoreCase) &&
            IsDownloadHost(asset.DownloadUrl));
        if (checksum is null)
        {
            return null;
        }

        return (package.DownloadUrl, checksum.DownloadUrl);
    }

    public static bool IsDownloadHost(Uri url)
    {
        if (!string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return url.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || url.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)
            || url.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            || url.Host.Equals("githubusercontent.com", StringComparison.OrdinalIgnoreCase);
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
}
