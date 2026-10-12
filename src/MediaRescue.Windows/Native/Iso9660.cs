using System.Globalization;
using System.Text;

namespace MediaRescue.Windows.Native;

/// <summary>Minimal ISO 9660 primary volume descriptor parsing (sector 16).</summary>
internal static class Iso9660
{
    public const int SectorSize = 2048;
    public const int PrimaryDescriptorSector = 16;

    public static bool IsPrimaryDescriptor(ReadOnlySpan<byte> s) =>
        s.Length >= SectorSize && s[0] == 1 && s[1] == (byte)'C' && s[2] == (byte)'D' && s[3] == (byte)'0' && s[4] == (byte)'0' && s[5] == (byte)'1';

    /// <summary>Volume creation date (offset 813); falls back to the root directory record time (offset 156+18).</summary>
    public static DateTime? ParseRecordedAtUtc(ReadOnlySpan<byte> s)
    {
        if (!IsPrimaryDescriptor(s)) return null;
        return ParseLongDate(s.Slice(813, 17)) ?? ParseLongDate(s.Slice(830, 17)) ?? ParseRootRecordDate(s);
    }

    private static DateTime? ParseLongDate(ReadOnlySpan<byte> d)
    {
        var text = Encoding.ASCII.GetString(d.Slice(0, 16));
        if (text.Length < 16 || !text.All(char.IsAsciiDigit)) return null;
        if (!int.TryParse(text.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out int y) || y < 1) return null;
        if (!TryDate(y, Num(text, 4), Num(text, 6), Num(text, 8), Num(text, 10), Num(text, 12), out var local)) return null;
        int tz = (sbyte)d[16]; // 15 minute units from GMT
        return DateTime.SpecifyKind(local.AddMinutes(-tz * 15), DateTimeKind.Utc);
    }

    private static int Num(string s, int i) => int.Parse(s.AsSpan(i, 2), NumberStyles.None, CultureInfo.InvariantCulture);

    private static DateTime? ParseRootRecordDate(ReadOnlySpan<byte> s)
    {
        var rec = s.Slice(156, 34);
        if (rec[0] < 34) return null;
        int y = 1900 + rec[18];
        if (!TryDate(y, rec[19], rec[20], rec[21], rec[22], rec[23], out var local)) return null;
        int tz = (sbyte)rec[24];
        return DateTime.SpecifyKind(local.AddMinutes(-tz * 15), DateTimeKind.Utc);
    }

    private static bool TryDate(int y, int mo, int d, int h, int mi, int s, out DateTime dt)
    {
        dt = default;
        if (y is < 1 or > 9999 || mo is < 1 or > 12 || d is < 1 or > 31 || h > 23 || mi > 59 || s > 59) return false;
        try { dt = new DateTime(y, mo, Math.Min(d, DateTime.DaysInMonth(y, mo)), h, mi, s, DateTimeKind.Unspecified); return true; }
        catch (ArgumentException) { return false; }
    }
}
