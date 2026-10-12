<#
.SYNOPSIS
  Downloads a pinned LGPL win64 ffmpeg build (ffmpeg.exe + ffprobe.exe) into <repo>\tools.
.DESCRIPTION
  Source : BtbN/FFmpeg-Builds (GitHub releases), LGPL variant (no GPL/nonfree components).
  Pinned : release tag autobuild-2026-08-31-13-27 -> ffmpeg n8.1.2-50-g1a748fe2cd (branch 8.1).
  The SHA-256 of the zip is pinned below and verified before extraction; the script is idempotent
  (does nothing when tools\ffmpeg.exe, tools\ffprobe.exe and the version marker already match).
  The binaries are NOT versioned in source control. This is a build-time download only; the app itself makes no network calls.
#>
[CmdletBinding()]
param(
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$Tag     = 'autobuild-2026-08-31-13-27'
$Asset   = 'ffmpeg-n8.1.2-50-g1a748fe2cd-win64-lgpl-8.1.zip'
$Url     = "https://github.com/BtbN/FFmpeg-Builds/releases/download/$Tag/$Asset"
$Sha256  = 'f6274bbd9c247f9e90c1bbed066b03ed4a3907cece2fb91be6dd352393936365'
$Marker  = "$Tag/$Asset/$Sha256"

$root  = Split-Path -Parent $PSScriptRoot
$tools = Join-Path $root 'tools'
$versionFile = Join-Path $tools 'VERSION.txt'

if (-not $Force -and (Test-Path (Join-Path $tools 'ffmpeg.exe')) -and (Test-Path (Join-Path $tools 'ffprobe.exe')) -and
    (Test-Path $versionFile) -and ((Get-Content $versionFile -Raw).Trim() -eq $Marker)) {
    Write-Host "tools\ already up to date ($Asset)."
    exit 0
}

$drive = Get-PSDrive -Name ((Get-Item $root).PSDrive.Name)
if ($drive.Free -lt 1GB) { throw "Espaco livre insuficiente (< 1 GB) em $($drive.Name):. Abortando." }

New-Item -ItemType Directory -Force $tools | Out-Null
$work = Join-Path ([IO.Path]::GetTempPath()) ("mediarescue-ffmpeg-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $work | Out-Null
try {
    $zip = Join-Path $work $Asset
    Write-Host "Downloading $Url"
    $ProgressPreference = 'SilentlyContinue'
    Invoke-WebRequest -Uri $Url -OutFile $zip -UseBasicParsing
    $actual = (Get-FileHash -Algorithm SHA256 -Path $zip).Hash.ToLowerInvariant()
    if ($actual -ne $Sha256) { throw "SHA-256 mismatch for $Asset. expected=$Sha256 actual=$actual" }
    Write-Host "SHA-256 OK ($actual)"

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $za = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        foreach ($name in 'ffmpeg.exe', 'ffprobe.exe') {
            $entry = $za.Entries | Where-Object { $_.FullName -match "/bin/$([regex]::Escape($name))$" } | Select-Object -First 1
            if (-not $entry) { throw "$name not found in archive" }
            $target = Join-Path $tools $name
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
            Write-Host "Extracted $name ($([math]::Round($entry.Length/1MB,1)) MB)"
        }
    } finally { $za.Dispose() }
    Set-Content -Path $versionFile -Value $Marker -Encoding ascii
    & (Join-Path $tools 'ffmpeg.exe') -hide_banner -version | Select-Object -First 1
} finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
