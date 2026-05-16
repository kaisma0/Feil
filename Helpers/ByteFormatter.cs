namespace Feil.Helpers;

/// <summary>
/// Shared formatting helpers for the UI layer.
/// Keeps FormatBytes and FormatSpeed logic in one place 
/// </summary>
public static class ByteFormatter
{
    public static string FormatBytes(long bytes)
    {
        if (bytes >= 1_099_511_627_776L) return $"{bytes / 1_099_511_627_776.0:F1} TB";
        if (bytes >= 1_073_741_824L)     return $"{bytes / 1_073_741_824.0:F1} GB";
        if (bytes >= 1_048_576L)         return $"{bytes / 1_048_576.0:F1} MB";
        if (bytes >= 1_024L)             return $"{bytes / 1_024.0:F1} KB";
        return $"{bytes} B";
    }

    public static string FormatSpeed(double bytesPerSecond)
    {
        var bitsPerSecond = bytesPerSecond * 8.0;
        if (bitsPerSecond >= 1_000_000_000.0) return $"{bitsPerSecond / 1_000_000_000.0:F1} Gbps";
        if (bitsPerSecond >= 1_000_000.0)     return $"{bitsPerSecond / 1_000_000.0:F1} Mbps";
        if (bitsPerSecond >= 1_000.0)         return $"{bitsPerSecond / 1_000.0:F1} Kbps";
        return $"{bitsPerSecond:F0} bps";
    }

    public static string FormatEta(TimeSpan eta)
    {
        if (eta == TimeSpan.Zero) return "--:--";
        if (eta.TotalHours >= 1) return $"{(int)eta.TotalHours:D2}:{eta.Minutes:D2}:{eta.Seconds:D2}";
        return $"{eta.Minutes:D2}:{eta.Seconds:D2}";
    }

    public static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)   return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        if (duration.TotalMinutes >= 1) return $"{(int)duration.TotalMinutes}m {duration.Seconds}s";
        return $"{duration.Seconds}s";
    }
}
