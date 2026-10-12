<#
.SYNOPSIS
  Generates tiny synthetic test videos (testsrc + sine) into <repo>\.testmedia using tools\ffmpeg.exe.
.DESCRIPTION
  Produces: dvd.vob (-target ntsc-dvd), vcd.mpg (-target ntsc-vcd), sample.avi, sample.wmv, sample.mp4.
  Each clip is 3-5 seconds. Total size is checked to stay far below 300 MB. Idempotent (existing files are kept unless -Force).
  Run build\fetch-tools.ps1 first.
#>
[CmdletBinding()]
param(
    [switch]$Force
)
$ErrorActionPreference = 'Stop'

$root  = Split-Path -Parent $PSScriptRoot
$ffmpeg = Join-Path $root 'tools\ffmpeg.exe'
$out   = Join-Path $root '.testmedia'
if (-not (Test-Path $ffmpeg)) { throw "ffmpeg.exe not found at $ffmpeg. Run build\fetch-tools.ps1 first." }
New-Item -ItemType Directory -Force $out | Out-Null

function New-Clip {
    param([string]$Name, [int]$Seconds, [string]$Size, [string[]]$OutputArgs)
    $target = Join-Path $out $Name
    if ((Test-Path $target) -and -not $Force) { Write-Host "keep   $Name"; return }
    $args = @('-hide_banner', '-loglevel', 'error', '-y',
              '-f', 'lavfi', '-i', "testsrc=duration=${Seconds}:size=${Size}:rate=30000/1001",
              '-f', 'lavfi', '-i', "sine=frequency=440:duration=${Seconds}:sample_rate=48000") + $OutputArgs + @($target)
    & $ffmpeg @args
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $target)) { throw "ffmpeg failed for $Name (exit $LASTEXITCODE)" }
    Write-Host ("made   {0} ({1:N0} bytes)" -f $Name, (Get-Item $target).Length)
}

New-Clip -Name 'dvd.vob'    -Seconds 5 -Size '720x480' -OutputArgs @('-target', 'ntsc-dvd')
New-Clip -Name 'vcd.mpg'    -Seconds 4 -Size '352x240' -OutputArgs @('-target', 'ntsc-vcd')
New-Clip -Name 'sample.avi' -Seconds 4 -Size '320x240' -OutputArgs @('-c:v', 'mpeg4', '-q:v', '5', '-c:a', 'pcm_s16le')
New-Clip -Name 'sample.wmv' -Seconds 4 -Size '320x240' -OutputArgs @('-c:v', 'wmv2', '-b:v', '300k', '-c:a', 'wmav2')
New-Clip -Name 'sample.mp4' -Seconds 4 -Size '320x240' -OutputArgs @('-c:v', 'mpeg4', '-q:v', '5', '-c:a', 'aac', '-b:a', '96k', '-movflags', '+faststart')

$total = (Get-ChildItem $out -File | Measure-Object Length -Sum).Sum
Write-Host ("Total test media: {0:N1} MB" -f ($total / 1MB))
if ($total -gt 300MB) { throw "Test media exceeds 300 MB budget." }
