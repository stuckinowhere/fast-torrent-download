namespace FastTorrentDownload.Services;

public static class AppInfo
{
    private static readonly Version AssemblyVersion =
        typeof(AppInfo).Assembly.GetName().Version ?? new Version(0, 1, 0);

    /// <summary>Three-part version such as <c>0.5.2</c>, shared by the UI and every outbound User-Agent.</summary>
    public static string Version { get; } = $"{AssemblyVersion.Major}.{AssemblyVersion.Minor}.{AssemblyVersion.Build}";

    public static Version Parsed => AssemblyVersion;
}
