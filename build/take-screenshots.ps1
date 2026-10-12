<#
.SYNOPSIS
  Captures the demo screens of the published MediaRescue.exe into docs\screenshots\*.png.
.DESCRIPTION
  Runs `MediaRescue.exe --demo --demo-state=<state>` (nothing touches real hardware), waits for the
  window, captures it with PrintWindow (works even if the window is partly covered) and closes it.
  Windows PowerShell 5.1. Run build\publish.ps1 first (uses dist\MediaRescue-<version>-win-x64\MediaRescue.exe).
#>
[CmdletBinding()]
param(
    [string]$Exe,
    [string]$ExeArgsPrefix,                  # optional: arguments placed before the demo flags (e.g. a .dll path when -Exe is dotnet.exe)
    [string]$OutDir,
    [string[]]$Only,                         # capture only these screens (e.g. -Only ready,history); default: all
    [string[]]$Themes = @('dark', 'light'),  # dark keeps the plain file name, light gets "-light"
    [string]$Size,                           # e.g. 1100x700 (sets MEDIARESCUE_DEMO_SIZE, demo mode only)
    [string]$FileTag                         # appended to every file name, e.g. "-min"
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Exe) {
    $v = (Get-Content (Join-Path $PSScriptRoot 'VERSION') -Raw).Trim()
    $Exe = Join-Path $root "dist\MediaRescue-$v-win-x64\MediaRescue.exe"
}
if (-not $OutDir) { $OutDir = Join-Path $root 'docs\screenshots' }
if (-not (Test-Path $Exe)) { throw "Exe not found: $Exe (run build\publish.ps1 first)" }
New-Item -ItemType Directory -Force $OutDir | Out-Null

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
public static class WinCap {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    public static void Init() { SetProcessDPIAware(); }
    // Scrolls the content under the point (offset from the window top-left, physical pixels) by mouse-wheel notches (negative = down).
    public static void Wheel(IntPtr h, int dx, int dy, int notches) {
        RECT r; GetWindowRect(h, out r);
        int x = r.Left + dx, y = r.Top + dy;
        IntPtr wp = (IntPtr)(((int)(short)(120 * notches)) << 16);
        IntPtr lp = (IntPtr)((y << 16) | (x & 0xFFFF));
        PostMessage(h, 0x020A, wp, lp);
    }
    public static List<IntPtr> WindowsOf(uint pid) {
        var list = new List<IntPtr>();
        EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) list.Add(h); return true; }, IntPtr.Zero);
        return list;
    }
    public static string Title(IntPtr h) { var sb = new StringBuilder(256); GetWindowText(h, sb, 256); return sb.ToString(); }
    public static string Capture(IntPtr h, string path) {
        RECT r;
        if (DwmGetWindowAttribute(h, 9, out r, Marshal.SizeOf(typeof(RECT))) != 0) GetWindowRect(h, out r);
        int w = r.Right - r.Left, ht = r.Bottom - r.Top;
        if (w <= 0 || ht <= 0) return "empty";
        RECT full; GetWindowRect(h, out full);
        int fw = full.Right - full.Left, fh = full.Bottom - full.Top;
        using (var bmp = new Bitmap(fw, fh, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(bmp)) { IntPtr hdc = g.GetHdc(); PrintWindow(h, hdc, 2); g.ReleaseHdc(hdc); }
            // crop invisible resize borders (DWM frame bounds are inside the window rect)
            var crop = new Rectangle(r.Left - full.Left, r.Top - full.Top, w, ht);
            crop.Intersect(new Rectangle(0, 0, fw, fh));
            using (var c = bmp.Clone(crop, bmp.PixelFormat)) { c.Save(path, ImageFormat.Png); }
        }
        return w + "x" + ht;
    }
}
"@ -ReferencedAssemblies System.Drawing
[WinCap]::Init()

# Scrolls the biggest vertically scrollable element of the window (UI Automation) to <Percent> % of its range.
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
function Set-LargestScroll([IntPtr]$Hwnd, [double]$Percent) {
    $ae = [System.Windows.Automation.AutomationElement]
    $root = $ae::FromHandle($Hwnd)
    $cond = New-Object System.Windows.Automation.PropertyCondition($ae::IsScrollPatternAvailableProperty, $true)
    $best = $null; $bestArea = 0
    foreach ($el in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) {
        $sp = $el.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
        if (-not $sp.Current.VerticallyScrollable) { continue }
        $r = $el.Current.BoundingRectangle
        if ($r.Width * $r.Height -gt $bestArea) { $bestArea = $r.Width * $r.Height; $best = $sp }
    }
    if ($best) { $best.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, $Percent) } else { Write-Host '  (no scrollable element found)' }
}

function Save-Demo {
    param([string]$Name, [string]$ArgsLine, [double[]]$Delays, [string[]]$Suffixes, [double]$WheelNotches = 0)
    $psi = New-Object Diagnostics.ProcessStartInfo
    $psi.FileName = $Exe
    $psi.Arguments = if ($ExeArgsPrefix) { "$ExeArgsPrefix $ArgsLine" } else { $ArgsLine }
    $psi.UseShellExecute = $false
    if ($Size) { $psi.EnvironmentVariables['MEDIARESCUE_DEMO_SIZE'] = $Size }
    $p = [Diagnostics.Process]::Start($psi)
    try {
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $first = $null
        while ($sw.Elapsed.TotalSeconds -lt 30) {
            $p.Refresh()
            if ($p.HasExited) { throw "process exited early with code $($p.ExitCode)" }
            if ($p.MainWindowHandle -ne [IntPtr]::Zero) { $first = $sw.ElapsedMilliseconds; break }
            Start-Sleep -Milliseconds 50
        }
        if ($null -eq $first) { throw 'no window within 30 s' }
        Write-Host ("{0}: window visible after {1} ms" -f $Name, $first)
        for ($i = 0; $i -lt $Delays.Count; $i++) {
            $target = $Delays[$i] * 1000
            while ($sw.ElapsedMilliseconds -lt $target) { Start-Sleep -Milliseconds 100 }
            $suffix = if ($Suffixes -and $Suffixes[$i]) { $Suffixes[$i] } else { '' }
            foreach ($h in [WinCap]::WindowsOf([uint32]$p.Id)) {
                $title = [WinCap]::Title($h)
                if ([string]::IsNullOrEmpty($title)) { continue }   # tooltips / popups
                $tag = if ($title -eq 'Extrator de M' + [char]0x00ED + 'dias') { '' } else { '-dialog' }
                $file = Join-Path $OutDir ("{0}{1}{2}{3}{4}.png" -f $Name, $suffix, $script:ThemeSuffix, $tag, $FileTag)
                $size = [WinCap]::Capture($h, $file)
                Write-Host ("  saved {0} ({1}) title='{2}'" -f (Split-Path $file -Leaf), $size, $title)
            }
        }
        if ($WheelNotches -ne 0) {
            $h = ([WinCap]::WindowsOf([uint32]$p.Id) | Where-Object { [WinCap]::Title($_) -eq ('Extrator de M' + [char]0x00ED + 'dias') } | Select-Object -First 1)
            Set-LargestScroll $h $WheelNotches
            Start-Sleep -Milliseconds 1200
            $file = Join-Path $OutDir ("{0}-scrolled{1}{2}.png" -f $Name, $script:ThemeSuffix, $FileTag)
            Write-Host ("  saved {0} ({1})" -f (Split-Path $file -Leaf), [WinCap]::Capture($h, $file))
        }
    } finally {
        if (-not $p.HasExited) { try { $p.Kill() } catch { } ; $p.WaitForExit(5000) | Out-Null }
    }
}

# Screens to capture: name, demo state, seconds after launch at which to capture, file-name suffixes, scroll position (%) of the biggest list.
# (converting: the copy takes ~5 s, then the demo converter runs slowly so the progress card can be captured; converted: fast converter, result text.)
$script:ThemeSuffix = ''
$shots = @(
    @{ Name = 'nodrive';     State = 'nodrive';     At = @(4) },
    @{ Name = 'nodisc';      State = 'nodisc';      At = @(4) },
    @{ Name = 'ready';       State = 'ready';       At = @(10); Scroll = 55 },
    @{ Name = 'copying';     State = 'copying';     At = @(16) },
    @{ Name = 'partial';     State = 'partial';     At = @(7, 19); Suffixes = @('-copying', '') },
    @{ Name = 'converting';  State = 'converting';  At = @(22) },
    @{ Name = 'converted';   State = 'converted';   At = @(30) },
    @{ Name = 'diagnostics'; State = 'diagnostics'; At = @(10); Scroll = 100 },
    @{ Name = 'history';     State = 'history';     At = @(5) },
    @{ Name = 'settings';    State = 'settings';    At = @(6); Scroll = 100 },
    @{ Name = 'batch';       State = 'batch';       At = @(6) }
)
foreach ($theme in $Themes) {
    $script:ThemeSuffix = if ($theme -eq 'light') { '-light' } else { '' }
    foreach ($s in $shots) {
        if ($Only -and ($Only -notcontains $s.Name)) { continue }
        $suffixes = if ($s.Suffixes) { [string[]]$s.Suffixes } else { $null }
        $scroll = if ($s.Scroll) { [double]$s.Scroll } else { 0 }
        Save-Demo $s.Name ("--demo --demo-state={0} --theme={1}" -f $s.State, $theme) ([double[]]$s.At) $suffixes -WheelNotches $scroll
    }
}