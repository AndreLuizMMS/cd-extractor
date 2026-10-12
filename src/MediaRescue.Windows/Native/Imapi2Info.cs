using System.Runtime.InteropServices;

namespace MediaRescue.Windows.Native;

/// <summary>Result of an IMAPI2 media query (late bound COM; everything optional).</summary>
internal sealed record ImapiMediaInfo(string? PhysicalType, int? Status, long? TotalSectors, long? FreeSectors)
{
    // IMAPI_FORMAT2_DATA_MEDIA_STATE flags
    public bool IsBlank => Status is { } s && (s & 0x2) != 0;
    public bool IsAppendable => Status is { } s && (s & 0x4) != 0 && (s & 0x2) == 0;
    public bool IsFinalized => Status is { } s && (s & 0x4000) != 0;
    public bool IsDamaged => Status is { } s && (s & 0x400) != 0;
}

/// <summary>
/// IMAPI2 (IDiscMaster2 / IDiscRecorder2 / IDiscFormat2Data) access through late binding so the app never depends on
/// interop assemblies. Read-only drives and virtual drives are usually "unsupported recorders": the result is then null.
/// </summary>
internal static class Imapi2Info
{
    public static readonly string?[] PhysicalTypeNames =
    {
        null, "CD-ROM", "CD-R", "CD-RW", "DVD-ROM", "DVD-RAM", "DVD+R", "DVD+RW", "DVD+R DL", "DVD-R", "DVD-RW",
        "DVD-R DL", "Disco", "DVD+RW DL", "HD DVD-ROM", "HD DVD-R", "HD DVD-RAM", "BD-ROM", "BD-R", "BD-RE",
    };

    public static ImapiMediaInfo? Query(string letter, TimeSpan timeout)
    {
        ImapiMediaInfo? result = null;
        var t = new Thread(() =>
        {
            try { result = QueryCore(letter); }
            catch { result = null; }
        }) { IsBackground = true, Name = "MediaRescue.Imapi" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return t.Join(timeout) ? result : null;
    }

    private static ImapiMediaInfo? QueryCore(string letter)
    {
        var masterType = Type.GetTypeFromProgID("IMAPI2.MsftDiscMaster2");
        var recType = Type.GetTypeFromProgID("IMAPI2.MsftDiscRecorder2");
        var fmtType = Type.GetTypeFromProgID("IMAPI2.MsftDiscFormat2Data");
        if (masterType == null || recType == null || fmtType == null) return null;

        dynamic? master = null; // late bound COM
        try
        {
            master = Activator.CreateInstance(masterType)!;
            if (!(bool)master.IsSupportedEnvironment) return null;
            int count = (int)master.Count;
            for (int i = 0; i < count; i++)
            {
                string id = (string)master[i];
                dynamic? rec = null;
                dynamic? fmt = null;
                try
                {
                    rec = Activator.CreateInstance(recType);
                    if (rec == null) continue;
                    rec.InitializeDiscRecorder(id);
                    if (!OwnsLetter(rec, letter)) continue;

                    fmt = Activator.CreateInstance(fmtType);
                    if (fmt == null) return null;
                    if (!(bool)fmt.IsRecorderSupported(rec)) return null;
                    fmt.Recorder = rec;
                    fmt.ClientName = "MediaRescue";

                    string? type = null;
                    int? status = null;
                    long? total = null, free = null;
                    try { int p = (int)fmt.CurrentPhysicalMediaType; if (p > 0 && p < PhysicalTypeNames.Length) type = PhysicalTypeNames[p]; } catch { }
                    try { status = (int)fmt.CurrentMediaStatus; } catch { }
                    try { total = (int)fmt.TotalSectorsOnMedia; } catch { }
                    try { free = (int)fmt.FreeSectorsOnMedia; } catch { }
                    if (type == null && status == null) return null;
                    return new ImapiMediaInfo(type, status, total, free);
                }
                catch
                {
                    // try next recorder
                }
                finally
                {
                    Release(fmt);
                    Release(rec);
                }
            }
            return null;
        }
        finally
        {
            Release(master);
        }
    }

    private static bool OwnsLetter(dynamic rec, string letter)
    {
        try
        {
            object? names = rec.VolumePathNames;
            if (names is Array arr)
            {
                foreach (var n in arr)
                    if (n is string s && s.Length >= 2 && char.ToUpperInvariant(s[0]) == char.ToUpperInvariant(letter[0]) && s[1] == ':')
                        return true;
            }
        }
        catch { }
        return false;
    }

    private static void Release(object? com)
    {
        try { if (com != null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com); } catch { }
    }
}

