using System.Management;

namespace MediaRescue.Windows.Native;

/// <summary>Win32_CDROMDrive inventory (slow, so only used as a fallback and by the diagnostics).</summary>
internal static class WmiInventory
{
    public sealed record CdromEntry(string Letter, string Caption, string Manufacturer, string Revision, string PnpId, string Status, int ConfigErrorCode)
    {
        /// <summary>Bus guessed from the PnP id prefix (USBSTOR, SCSI, IDE, ...).</summary>
        public string Bus
        {
            get
            {
                var p = PnpId.ToUpperInvariant();
                if (p.StartsWith("USBSTOR")) return "USB";
                if (p.StartsWith("IDE")) return "ATA";
                if (p.StartsWith("SCSI")) return "SCSI";
                if (p.StartsWith("SATA")) return "SATA";
                return "";
            }
        }
    }

    public static IReadOnlyList<CdromEntry> Query(TimeSpan timeout)
    {
        IReadOnlyList<CdromEntry> result = Array.Empty<CdromEntry>();
        var t = new Thread(() =>
        {
            try { result = QueryCore(timeout); } catch { }
        }) { IsBackground = true, Name = "MediaRescue.Wmi" };
        t.Start();
        return t.Join(timeout + TimeSpan.FromSeconds(2)) ? result : Array.Empty<CdromEntry>();
    }

    private static List<CdromEntry> QueryCore(TimeSpan timeout)
    {
        var list = new List<CdromEntry>();
        var options = new System.Management.EnumerationOptions { Timeout = timeout, ReturnImmediately = false };
        using var searcher = new ManagementObjectSearcher(
            new ManagementScope(@"\\.\root\cimv2"),
            new ObjectQuery("SELECT Drive, Caption, Manufacturer, MfrAssignedRevisionLevel, PNPDeviceID, Status, ConfigManagerErrorCode FROM Win32_CDROMDrive"),
            options);
        foreach (ManagementBaseObject o in searcher.Get())
        {
            try
            {
                var letter = (o["Drive"] as string) ?? "";
                list.Add(new CdromEntry(
                    letter.Trim().ToUpperInvariant(),
                    ((o["Caption"] as string) ?? "").Trim(),
                    ((o["Manufacturer"] as string) ?? "").Trim(),
                    ((o["MfrAssignedRevisionLevel"] as string) ?? "").Trim(),
                    ((o["PNPDeviceID"] as string) ?? "").Trim(),
                    ((o["Status"] as string) ?? "").Trim(),
                    o["ConfigManagerErrorCode"] is { } c ? Convert.ToInt32(c) : 0));
            }
            finally { o.Dispose(); }
        }
        return list;
    }
}

