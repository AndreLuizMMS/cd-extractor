using System.Globalization;

namespace MediaRescue.Core;

/// <summary>pt-BR formatting helpers independent of the machine's ICU/NLS data.</summary>
internal static class Fmt
{
    private static readonly NumberFormatInfo Pt = new()
    {
        NumberDecimalSeparator = ",",
        NumberGroupSeparator = ".",
    };

    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

    public static string Bytes(long bytes)
    {
        if (bytes < 0) bytes = 0;
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < Units.Length - 1) { v /= 1024; i++; }
        if (i == 0) return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        return v.ToString("0.##", Pt) + " " + Units[i];
    }

    public static string Percent(double fraction) => (fraction * 100).ToString("0.##", Pt) + "%";

    public static string Date(DateTime utc) => utc.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

    public static string Hex(byte[] hash) => Convert.ToHexString(hash);
}
