$ErrorActionPreference = "Stop"
$hostDir = "D:\Desktop\高亮\semantic-overlay\native-host"
$outputDir = Join-Path $hostDir "bin"
$compiler = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$naudio = Join-Path (Split-Path -Parent $hostDir) "vendor\NAudio\NAudio.dll"
$nativeSources = @(Get-ChildItem -LiteralPath $hostDir -Filter "*.cs" -File |
    Sort-Object Name | ForEach-Object { $_.FullName })
& $compiler /nologo /target:exe /platform:x64 /optimize+ `
    /main:SemanticOverlay.NativeHost.FloatPlacementTest `
    /out:"$outputDir\FloatPlacementTest.exe" `
    /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll `
    /reference:System.Security.dll /reference:"$naudio" `
    @nativeSources `
    "$hostDir\diagnostics\FloatPlacementTest.cs"
if ($LASTEXITCODE -ne 0) { throw "FloatPlacementTest compilation failed." }
& $compiler /nologo /target:exe /platform:x64 /optimize+ `
    /main:SemanticOverlay.NativeHost.SelectionLookupInteractionTest `
    /out:"$outputDir\SelectionLookupInteractionTest.exe" `
    /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll `
    /reference:System.Security.dll /reference:"$naudio" `
    @nativeSources `
    "$hostDir\diagnostics\SelectionLookupInteractionTest.cs"
if ($LASTEXITCODE -ne 0) { throw "SelectionLookupInteractionTest compilation failed." }
& $compiler /nologo /target:exe /platform:x64 /optimize+ `
    /main:SemanticOverlay.NativeHost.ContinuousLookupTest `
    /out:"$outputDir\ContinuousLookupTest.exe" `
    /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll `
    /reference:System.Security.dll /reference:"$naudio" `
    @nativeSources `
    "$hostDir\diagnostics\ContinuousLookupTest.cs"
if ($LASTEXITCODE -ne 0) { throw "ContinuousLookupTest compilation failed." }
Write-Output "manual-diagnostics-build-ok"
