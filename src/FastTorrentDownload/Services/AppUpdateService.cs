using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;

namespace FastTorrentDownload.Services;

public sealed class AppUpdateService
{
    private static readonly HttpClient Client = CreateClient();
    private const int MaxChecksumBytes = 4096;

    public async Task<string> DownloadAndVerifyAsync(
        AvailableUpdate update,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // The tag becomes a folder name and is echoed by the updater script: accept version text only.
        if (!IsSafeTag(update.Tag))
        {
            throw new InvalidDataException("The release tag is not a plain version number; the update was not downloaded.");
        }

        var workDirectory = Directory.CreateDirectory(Path.Combine(
            Path.GetTempPath(), "fast-torrent-download-update", update.Tag.TrimStart('v', 'V'))).FullName;
        var packagePath = Path.Combine(workDirectory, "update-portable.zip");

        await DownloadAsync(update.PackageUrl, packagePath, progress, cancellationToken);
        var expectedHash = await ReadExpectedHashAsync(update.ChecksumUrl, cancellationToken);
        var actualHash = await HashFileAsync(packagePath, cancellationToken);
        if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(packagePath); } catch (IOException) { }
            throw new InvalidDataException("The update package failed its checksum verification and was discarded.");
        }

        AppLogger.Log($"Update package verified: {update.Tag} sha256={actualHash}");
        return packagePath;
    }

    public static bool IsSafeTag(string? tag) =>
        tag is not null && System.Text.RegularExpressions.Regex.IsMatch(tag, @"^[vV]?\d{1,5}(\.\d{1,5}){1,3}$");

    public void StageAndLaunchInstaller(string packagePath, string tag)
    {
        if (!IsSafeTag(tag))
        {
            throw new InvalidDataException("The release tag is not a plain version number; the update was not installed.");
        }

        var installDirectory = AppContext.BaseDirectory;
        var executablePath = Path.Combine(installDirectory, "FastTorrentDownload.exe");
        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException("Cannot self-update: the application executable was not found next to the running app.");
        }

        var workDirectory = Path.GetDirectoryName(packagePath)
            ?? throw new InvalidOperationException("Cannot self-update: the package path has no directory.");
        var stageDirectory = Path.Combine(workDirectory, "staged");
        // Start from an empty folder: leftovers from an earlier attempt must never be copied into the install.
        if (Directory.Exists(stageDirectory))
        {
            Directory.Delete(stageDirectory, recursive: true);
        }

        Directory.CreateDirectory(stageDirectory);
        ZipFile.ExtractToDirectory(packagePath, stageDirectory, overwriteFiles: true);
        if (!File.Exists(Path.Combine(stageDirectory, "FastTorrentDownload.exe")))
        {
            throw new InvalidDataException("The update package does not contain the application executable.");
        }

        var scriptPath = Path.Combine(workDirectory, "apply-update.ps1");
        File.WriteAllText(scriptPath, BuildUpdaterScript(
            Environment.ProcessId, stageDirectory, installDirectory, executablePath, workDirectory, tag));
        Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath()
        });
        AppLogger.Log($"Update installer staged for {tag}; exiting for replacement.");
    }

    public static string? ParseChecksumFile(string content, string packageFileName)
    {
        foreach (var line in content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[^1].TrimStart('*').Equals(packageFileName, StringComparison.OrdinalIgnoreCase))
            {
                return parts[0].Length == 64 ? parts[0] : null;
            }
        }

        return null;
    }

    private static string PackageFileName(Uri packageUrl) =>
        Path.GetFileName(Uri.UnescapeDataString(packageUrl.AbsolutePath)) is { Length: > 0 } name
            ? name
            : "update-portable.zip";

    private static async Task DownloadAsync(Uri url, string destinationPath, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is { } finalUri && !ReleaseUpdateService.IsDownloadHost(finalUri))
        {
            throw new InvalidOperationException("The update download redirected to an untrusted host.");
        }

        var totalBytes = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        var buffer = new byte[81920];
        long received = 0;
        int read;
        while ((read = await source.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            received += read;
            if (totalBytes > 0)
            {
                progress?.Report((double)received / totalBytes.Value);
            }
        }

        progress?.Report(1);
    }

    private async Task<string> ReadExpectedHashAsync(Uri checksumUrl, CancellationToken cancellationToken)
    {
        using var response = await Client.GetAsync(checksumUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is { } finalUri && !ReleaseUpdateService.IsDownloadHost(finalUri))
        {
            throw new InvalidOperationException("The update checksum redirected to an untrusted host.");
        }

        // A checksum file is one short line; refuse anything that is not.
        if (response.Content.Headers.ContentLength is > MaxChecksumBytes)
        {
            throw new InvalidDataException("The update checksum file is unexpectedly large.");
        }

        await response.Content.LoadIntoBufferAsync(MaxChecksumBytes, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        // The checksum asset is named "<package>.sha256"; recover the package file name from it.
        var checksumName = PackageFileName(checksumUrl);
        var packageName = checksumName.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase)
            ? checksumName[..^".sha256".Length]
            : checksumName;
        return ParseChecksumFile(content, packageName)
            ?? throw new InvalidDataException("The update checksum file is missing or malformed.");
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    private static string BuildUpdaterScript(int processId, string stageDirectory, string installDirectory, string executablePath, string workDirectory, string tag)
    {
        // Single quotes inside PowerShell single-quoted strings are escaped by doubling them.
        static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
        return $$"""
            $ErrorActionPreference = 'Stop'
            $log = {{Quote(Path.Combine(workDirectory, "apply-update.log"))}}
            "Waiting for process {{processId}} to exit (update {{tag}})" | Out-File $log
            $deadline = (Get-Date).AddMinutes(2)
            while ((Get-Process -Id {{processId}} -ErrorAction SilentlyContinue) -ne $null -and (Get-Date) -lt $deadline) {
                Start-Sleep -Milliseconds 250
            }
            Start-Sleep -Seconds 1
            "Copying staged files" | Out-File $log -Append
            Copy-Item (Join-Path {{Quote(stageDirectory)}} '*') -Destination {{Quote(installDirectory)}} -Recurse -Force
            "Starting updated app" | Out-File $log -Append
            Start-Process -FilePath {{Quote(executablePath)}} -WorkingDirectory {{Quote(installDirectory)}}
            "Update {{tag}} applied" | Out-File $log -Append
            """;
    }

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            ConnectTimeout = TimeSpan.FromSeconds(15)
        };
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(15)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"fast-torrent-download-updater/{AppInfo.Version}");
        return client;
    }
}
