param(
    [string]$OutputPath = (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) "RealtimeDictionary-portable.zip"),
    [string]$RuntimePath = ""
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$nativeBin = Join-Path $projectDir "native-host\bin\SemanticOverlay.exe"
$nativeSources = @(
    (Join-Path $projectDir "native-host\Program.cs"),
    (Join-Path $projectDir "native-host\LocalReminders.cs"),
    (Join-Path $projectDir "native-host\AudioCapture.cs"),
    (Join-Path $projectDir "native-host\ProcessLoopbackAudioClient.cs"),
    (Join-Path $projectDir "native-host\build.ps1"),
    (Join-Path $projectDir "native-host\windows_ocr.ps1"),
    (Join-Path $projectDir "native-host\windows_ocr_worker.ps1"),
    (Join-Path $projectDir "vendor\NAudio\NAudio.dll")
)
$latestNativeSource = $nativeSources | Where-Object { Test-Path -LiteralPath $_ } |
    ForEach-Object { (Get-Item -LiteralPath $_).LastWriteTime } |
    Sort-Object -Descending | Select-Object -First 1

if (-not (Test-Path $nativeBin) -or
    $latestNativeSource -gt (Get-Item $nativeBin).LastWriteTime) {
    & (Join-Path $projectDir "native-host\build.ps1")
}
if (-not (Test-Path $nativeBin)) {
    throw "Native host build did not produce SemanticOverlay.exe."
}

$parent = Split-Path -Parent $OutputPath
if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
if (Test-Path -LiteralPath $OutputPath) {
    throw "Output already exists; select a new versioned path."
}

$packageFiles = New-Object System.Collections.Generic.List[object]
function Add-PackageFile([string]$Source, [string]$EntryName) {
    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) {
        throw "Package source file is missing: $Source"
    }
    $packageFiles.Add([pscustomobject]@{
        Source = [IO.Path]::GetFullPath($Source)
        Entry = $EntryName.Replace('\', '/')
    })
}

$usageGuideName = ([string][char]0x4F7F) + ([char]0x7528) + ([char]0x8BF4) + ([char]0x660E) + ".md"
$rootFiles = @(
    $usageGuideName, "server.py", "ocr_service.py", "calendar_export.py", "outlook_calendar.py",
    "start.cmd", "start.ps1", "OUTLOOK_SETUP.md"
)
foreach ($name in $rootFiles) {
    Add-PackageFile (Join-Path $projectDir $name) $name
}
Add-PackageFile $nativeBin "native-host/bin/SemanticOverlay.exe"
Add-PackageFile (Join-Path $projectDir "native-host\bin\NAudio.dll") "native-host/bin/NAudio.dll"
Add-PackageFile (Join-Path $projectDir "vendor\NAudio\LICENSE.txt") "THIRD_PARTY_LICENSES/NAudio.txt"
Add-PackageFile (Join-Path $projectDir "native-host\windows_ocr.ps1") "native-host/windows_ocr.ps1"
Add-PackageFile (Join-Path $projectDir "native-host\windows_ocr_worker.ps1") "native-host/windows_ocr_worker.ps1"

$extensionDir = Join-Path $projectDir "..\browser-extension"
foreach ($name in @("manifest.json", "content.js", "styles.css", "background.js", "popup.html", "popup.js")) {
    Add-PackageFile (Join-Path $extensionDir $name) ("browser-extension/" + $name)
}

if ($RuntimePath) {
    $runtimeRoot = [IO.Path]::GetFullPath($RuntimePath).TrimEnd('\', '/')
    if (-not (Test-Path -LiteralPath (Join-Path $runtimeRoot "python.exe"))) {
        throw "RuntimePath must point to a directory containing python.exe."
    }
    foreach ($file in Get-ChildItem -LiteralPath $runtimeRoot -File -Recurse) {
        $relative = $file.FullName.Substring($runtimeRoot.Length).TrimStart('\', '/')
        Add-PackageFile $file.FullName ("runtime/" + $relative)
    }
}

$archive = [IO.Compression.ZipFile]::Open(
    [IO.Path]::GetFullPath($OutputPath), [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($item in $packageFiles) {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $archive, $item.Source, $item.Entry,
            [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
}
finally {
    $archive.Dispose()
}
Write-Host "Created $OutputPath"
if (-not $RuntimePath) {
    Write-Warning "No bundled Python runtime was supplied; recipients need Python on PATH."
}
