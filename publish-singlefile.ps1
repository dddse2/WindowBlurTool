<#
.SYNOPSIS
    Publish BlurTool.

.DESCRIPTION
    Two verified modes:

    Small (default)
      Framework-dependent single-file exe, about 42 MB (exe + two pri files).
      Requires .NET 8 Desktop Runtime and Windows App Runtime on the target machine.

    Portable
      Self-contained folder, about 145 MB. No runtime required.

    Notes about size:
    - WinUI 3 does not support reliable IL trimming, so PublishTrimmed stays false.
    - Single-file compression breaks WinUI resource loading
      (ms-appx:///Microsoft.UI.Xaml/Themes/themeresources.xaml), so it is not used.
    - Single-file publishing does not place the pri files next to the exe, but the
      ms-appx resolver needs them there, so this script copies them from bin output.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\publish-singlefile.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\publish-singlefile.ps1 -Mode Portable
#>
[CmdletBinding()]
param(
    [ValidateSet("Small", "Portable")]
    [string]$Mode = "Small",
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "win-x64",
    [string]$OutputDir = ""
)

$ErrorActionPreference = "Stop"

$root = $PSScriptRoot
$project = Join-Path $root "BlurTool\BlurTool.csproj"

if (-not (Test-Path $project)) {
    throw "Project file not found: $project"
}

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $root ("publish\{0}-{1}" -f $Mode.ToLowerInvariant(), $RuntimeIdentifier)
}

$common = @(
    "-p:PublishTrimmed=false",
    "-p:PublishReadyToRun=false",
    "-p:DebugType=none"
)

if ($Mode -eq "Small") {
    $specific = @(
        "-p:PublishSingleFile=true",
        "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-p:IncludeAllContentForSelfExtract=true",
        "-p:SelfContained=false",
        "-p:WindowsAppSDKSelfContained=false"
    )
}
else {
    $specific = @(
        "-p:PublishSingleFile=false",
        "-p:SelfContained=true",
        "-p:WindowsAppSDKSelfContained=true"
    )
}

Write-Host ("Publishing {0} build..." -f $Mode) -ForegroundColor Cyan
Write-Host "  Config    : $Configuration"
Write-Host "  RID       : $RuntimeIdentifier"
Write-Host "  OutputDir : $OutputDir"

$arguments = @("publish", $project, "-c", $Configuration, "-r", $RuntimeIdentifier, "-o", $OutputDir) +
             $common + $specific

& dotnet @arguments
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

if ($Mode -eq "Small") {
    $binRoot = Join-Path $root "BlurTool\bin"
    $binDir = Get-ChildItem $binRoot -Recurse -Directory -Filter $RuntimeIdentifier -ErrorAction SilentlyContinue |
              Where-Object { $_.FullName.Contains("\$Configuration\") -and (Test-Path (Join-Path $_.FullName "resources.pri")) } |
              Sort-Object LastWriteTime -Descending |
              Select-Object -First 1

    if ($binDir) {
        Copy-Item (Join-Path $binDir.FullName "*.pri") $OutputDir -Force
        Write-Host "  pri       : copied next to the exe"
    }
    else {
        Write-Warning "pri files not found. The single-file build may fail to start."
    }
}

$exe = Join-Path $OutputDir "BlurTool.exe"
if (Test-Path $exe) {
    $size = (Get-Item $exe).Length / 1MB
    $total = (Get-ChildItem $OutputDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB

    Write-Host ""
    Write-Host ("Done: {0}" -f $exe) -ForegroundColor Green
    Write-Host ("  exe   : {0:N1} MB" -f $size)
    Write-Host ("  total : {0:N1} MB" -f $total)
}
else {
    Write-Host "Publish finished. Please check the output folder." -ForegroundColor Yellow
}
