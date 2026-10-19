namespace FastTorrentDownload.Services;

public static class TransferFormatting
{
    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.0} {units[unit]}";
    }

    public static string Rate(long bytesPerSecond) => $"{Bytes(bytesPerSecond)}/s";

    public static string Remaining(long verified, long total, long rate, string state)
    {
        if (total > 0 && verified >= total) return "Complete";
        if (total <= 0 || rate <= 0 || state != "Downloading") return "ETA unknown";
        var seconds = Math.Ceiling((double)(total - verified) / rate);
        if (seconds >= TimeSpan.MaxValue.TotalSeconds) return "ETA unknown";
        var eta = TimeSpan.FromSeconds(seconds);
        return eta.TotalDays >= 1 ? $"ETA {(int)eta.TotalDays}d {eta.Hours}h" :
            eta.TotalHours >= 1 ? $"ETA {eta.Hours}h {eta.Minutes}m" : $"ETA {eta.Minutes}m {eta.Seconds}s";
    }
}
