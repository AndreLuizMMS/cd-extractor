using System.Globalization;

namespace MediaRescue.App.Infrastructure;

/// <summary>pt-BR formatting helpers (sizes, durations, dates, percentages).</summary>
public static class Fmt
{
    public static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

    public static string Bytes(long bytes)
    {
        if (bytes < 0) bytes = 0;
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < Units.Length - 1) { v /= 1024; i++; }
        if (i == 0) return $"{bytes} B";
        return v.ToString(v >= 100 ? "0" : "0.0", Pt) + " " + Units[i];
    }

    public static string Speed(double bytesPerSecond) =>
        bytesPerSecond > 0 && !double.IsNaN(bytesPerSecond) && !double.IsInfinity(bytesPerSecond)
            ? Bytes((long)bytesPerSecond) + "/s"
            : "—";

    /// <summary>mm:ss, or h:mm:ss from one hour on. Negative values are shown as zero.</summary>
    public static string Clock(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes:00}:{t.Seconds:00}";
    }

    /// <summary>hh:mm:ss, as shown in the video duration column.</summary>
    public static string VideoDuration(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
    }

    public static string Date(DateTime utc) => utc.ToLocalTime().ToString("dd/MM/yyyy", Pt);

    public static string DateTimeText(DateTime utc) => utc.ToLocalTime().ToString("dd/MM/yyyy HH:mm", Pt);

    public static string Percent(double fraction)
    {
        if (double.IsNaN(fraction)) fraction = 0;
        return $"{(int)Math.Floor(Math.Clamp(fraction, 0, 1) * 100)}%";
    }

    public static string Plural(int n, string singular, string plural) => $"{n} {(n == 1 ? singular : plural)}";
}
