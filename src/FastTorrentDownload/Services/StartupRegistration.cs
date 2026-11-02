using Microsoft.Win32;

namespace FastTorrentDownload.Services;

/// <summary>Per-user "start with Windows" entry; no elevation, no machine-wide changes.</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "FastTorrentDownload";
    public const string MinimizedArgument = "--minimized";

    public static void Apply(bool enabled)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (key is null)
        {
            return;
        }

        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            throw new InvalidOperationException("The application path is unknown; cannot register startup.");
        }

        key.SetValue(ValueName, $"\"{executable}\" {MinimizedArgument}", RegistryValueKind.String);
    }
}
