<#
.SYNOPSIS
  Builds the portable release: dist\MediaRescue-<version>-win-x64\ and dist\MediaRescue-<version>-win-x64.zip.
.DESCRIPTION
  1. Finds dotnet (PATH, then %LOCALAPPDATA%\dotnet\dotnet.exe).
  2. Runs build\fetch-tools.ps1 when tools\ffmpeg.exe / tools\ffprobe.exe are missing.
  3. dotnet publish src\MediaRescue.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
     (own --artifacts-path: <root>\.artifacts\publish, so it never collides with other builds).
     The csproj is not touched: Version is passed on the command line (the csproj carries the same number as
     build\VERSION for development builds), and the published MediaRescue.App.exe is copied/renamed to MediaRescue.exe.
  4. Assembles the folder (MediaRescue.exe, tools\, LEIA-ME.txt, THIRD-PARTY-NOTICES.md, LICENSE),
     zips it and writes a .sha256 file next to the zip.
  The version defaults to the single line in build\VERSION. A leading "v" (git tag) is accepted.
.EXAMPLE
  .\build\publish.ps1
  .\build\publish.ps1 -Version v1.2.0
#>
[CmdletBinding()]
param(
    [string]$Version,
    [switch]$KeepArtifacts,   # keep <root>\.artifacts\publish after the build (default: delete it)
    [switch]$NoStagingFolder  # delete dist\MediaRescue-<version>-win-x64\ after zipping (keeps only the zip)
)
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot

# ---------------------------------------------------------------- version (single source: build\VERSION)
if ([string]::IsNullOrWhiteSpace($Version)) {
    $vf = Join-Path $PSScriptRoot 'VERSION'
    if (-not (Test-Path $vf)) { throw "build\VERSION not found and -Version not given." }
    $Version = (Get-Content $vf -Raw).Trim()
}
$Version = $Version.Trim() -replace '^[vV]', ''
if ($Version -notmatch '^\d+\.\d+\.\d+([-+][0-9A-Za-z.\-]+)?$') { throw "Invalid version '$Version' (expected e.g. 1.0.0 or 1.0.0-rc.1)." }

# ---------------------------------------------------------------- dotnet
$dotnet = $null
$cmd = Get-Command dotnet -ErrorAction SilentlyContinue
if ($cmd) { $dotnet = $cmd.Source }
if (-not $dotnet) {
    $candidate = Join-Path $env:LOCALAPPDATA 'dotnet\dotnet.exe'
    if (Test-Path $candidate) { $dotnet = $candidate }
}
if (-not $dotnet) { throw "dotnet not found (neither on PATH nor in %LOCALAPPDATA%\dotnet). Install the .NET 8 SDK." }
Write-Host "dotnet : $dotnet ($(& $dotnet --version))"
Write-Host "version: $Version"

# ---------------------------------------------------------------- disk space
$drive = Get-PSDrive -Name ((Get-Item $root).PSDrive.Name)
if ($drive.Free -lt 2GB) { throw "Less than 2 GB free on $($drive.Name):. Aborting." }

# ---------------------------------------------------------------- tools (ffmpeg / ffprobe)
$tools = Join-Path $root 'tools'
if (-not ((Test-Path (Join-Path $tools 'ffmpeg.exe')) -and (Test-Path (Join-Path $tools 'ffprobe.exe')))) {
    Write-Host 'tools\ffmpeg.exe / ffprobe.exe missing: running build\fetch-tools.ps1'
    & (Join-Path $PSScriptRoot 'fetch-tools.ps1')
    if (-not ((Test-Path (Join-Path $tools 'ffmpeg.exe')) -and (Test-Path (Join-Path $tools 'ffprobe.exe')))) { throw 'fetch-tools.ps1 did not produce ffmpeg.exe/ffprobe.exe.' }
}

# ---------------------------------------------------------------- publish
$project   = Join-Path $root 'src\MediaRescue.App\MediaRescue.App.csproj'
$artifacts = Join-Path $root '.artifacts\publish'
if (Test-Path $artifacts) { Remove-Item -Recurse -Force $artifacts }

$publishArgs = @(
    'publish', $project,
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', 'true',
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    "-p:Version=$Version",
    "-p:InformationalVersion=$Version",
    '--artifacts-path', $artifacts,
    '--nologo'
)
Write-Host "dotnet $($publishArgs -join ' ')"
& $dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }

# With --artifacts-path the final files land in <artifacts>\publish\<project>\<config>_<rid>\ (bin\ and obj\ hold intermediates).
$publishDir = Join-Path $artifacts 'publish'
$exe = Get-ChildItem -Path $publishDir -Recurse -File -Filter 'MediaRescue*.exe' |
    Where-Object { $_.Name -in @('MediaRescue.exe', 'MediaRescue.App.exe') } |
    Sort-Object @{ Expression = { if ($_.Name -eq 'MediaRescue.exe') { 0 } else { 1 } } } | Select-Object -First 1
if (-not $exe) { throw "Published exe not found under $artifacts." }
Write-Host "published: $($exe.FullName)"

# ---------------------------------------------------------------- assemble dist\
$name    = "MediaRescue-$Version-win-x64"
$dist    = Join-Path $root 'dist'
$stage   = Join-Path $dist $name
$zipPath = Join-Path $dist "$name.zip"
New-Item -ItemType Directory -Force $dist | Out-Null
if (Test-Path $stage)   { Remove-Item -Recurse -Force $stage }
if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
New-Item -ItemType Directory -Force (Join-Path $stage 'tools') | Out-Null

Copy-Item $exe.FullName (Join-Path $stage 'MediaRescue.exe')
foreach ($t in 'ffmpeg.exe', 'ffprobe.exe') { Copy-Item (Join-Path $tools $t) (Join-Path $stage "tools\$t") }
if (Test-Path (Join-Path $tools 'VERSION.txt')) { Copy-Item (Join-Path $tools 'VERSION.txt') (Join-Path $stage 'tools\VERSION.txt') }
foreach ($f in 'THIRD-PARTY-NOTICES.md', 'LICENSE') {
    $src = Join-Path $root $f
    if (Test-Path $src) { Copy-Item $src (Join-Path $stage $f) } else { Write-Warning "$f not found in repository root." }
}

# LEIA-ME.txt: UTF-8 with BOM so that Notepad shows the accents correctly.
$template = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'LEIA-ME.template.txt'), [Text.Encoding]::UTF8)
$leia = $template.Replace('{{VERSION}}', $Version) -replace "(?<!`r)`n", "`r`n"
[IO.File]::WriteAllText((Join-Path $stage 'LEIA-ME.txt'), $leia, (New-Object Text.UTF8Encoding($true)))

# ---------------------------------------------------------------- zip (forward-slash entries, top folder included)
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $zipPath, [IO.Compression.CompressionLevel]::Optimal, $true)

$zipHash = (Get-FileHash -Algorithm SHA256 -Path $zipPath).Hash.ToLowerInvariant()
$exeHash = (Get-FileHash -Algorithm SHA256 -Path (Join-Path $stage 'MediaRescue.exe')).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$zipPath.sha256", "$zipHash *$name.zip`n", (New-Object Text.UTF8Encoding($false)))

Write-Host ''
Write-Host '=== Result ==='
Write-Host ("MediaRescue.exe : {0:N1} MB  sha256={1}" -f ((Get-Item (Join-Path $stage 'MediaRescue.exe')).Length / 1MB), $exeHash)
Write-Host ("zip             : {0:N1} MB  {1}" -f ((Get-Item $zipPath).Length / 1MB), $zipPath)
Write-Host ("zip sha256      : {0}" -f $zipHash)
Write-Host ("sha256 file     : {0}.sha256" -f $zipPath)
$stageSize = (Get-ChildItem $stage -Recurse -File | Measure-Object Length -Sum).Sum
Write-Host ("folder          : {0:N1} MB  {1}" -f ($stageSize / 1MB), $stage)

if ($NoStagingFolder) { Remove-Item -Recurse -Force $stage }
if (-not $KeepArtifacts) { Remove-Item -Recurse -Force $artifacts -ErrorAction SilentlyContinue }

# Hand the paths to GitHub Actions (no-op elsewhere).
if ($env:GITHUB_OUTPUT) {
    Add-Content -Path $env:GITHUB_OUTPUT -Value "zip=$zipPath"
    Add-Content -Path $env:GITHUB_OUTPUT -Value "sha256=$zipPath.sha256"
    Add-Content -Path $env:GITHUB_OUTPUT -Value "version=$Version"
}
