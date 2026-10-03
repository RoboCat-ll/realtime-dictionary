param(
    [string]$PayloadPath = "",
    [string]$OutputPath = ""
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$versionFile = Join-Path $projectRoot 'version.txt'
$productVersion = (Get-Content -LiteralPath $versionFile -Raw).Trim()
if (-not $PayloadPath) { $PayloadPath = Join-Path $projectRoot "RealtimeDictionary-portable-v$productVersion.zip" }
if (-not $OutputPath) { $OutputPath = Join-Path $projectRoot "RealtimeDictionary-Setup-v$productVersion.exe" }
if (Test-Path -LiteralPath $OutputPath) { throw "Output already exists; select a new versioned path." }
$installerDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$source = Join-Path $installerDir "Setup.cs"
$compiler = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$framework = Split-Path -Parent $compiler

if (-not (Test-Path -LiteralPath $PayloadPath)) {
    throw "Portable payload not found: $PayloadPath"
}
if (-not (Test-Path -LiteralPath $compiler)) {
    throw "The built-in .NET Framework C# compiler was not found."
}

$parent = Split-Path -Parent $OutputPath
if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
$outputArg = "/out:$OutputPath"
$compressionReference = "/reference:$(Join-Path $framework 'System.IO.Compression.dll')"
$fileSystemReference = "/reference:$(Join-Path $framework 'System.IO.Compression.FileSystem.dll')"
$resourceArg = "/resource:$PayloadPath,RealtimeDictionary.Payload.zip"
$versionResource = "/resource:$versionFile,RealtimeDictionary.Version.txt"

& $compiler /nologo /target:winexe /platform:x64 /optimize+ `
    $outputArg `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll `
    $compressionReference `
    $fileSystemReference `
    $resourceArg `
    $versionResource `
    "$source"

if ($LASTEXITCODE -ne 0) {
    throw "Installer compilation failed with exit code $LASTEXITCODE."
}

Write-Host "Created $OutputPath"
