$ErrorActionPreference = "Stop"

$hostDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$outputDir = Join-Path $hostDir "bin"
$compiler = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$naudio = Join-Path (Split-Path -Parent $hostDir) "vendor\NAudio\NAudio.dll"

if (-not (Test-Path $compiler)) {
    throw "The built-in .NET Framework C# compiler was not found."
}

# Root C# files are production modules; diagnostic entry points are added separately.
$nativeSources = @(Get-ChildItem -LiteralPath $hostDir -Filter "*.cs" -File |
    Sort-Object Name | ForEach-Object { $_.FullName })

New-Item -ItemType Directory -Force -Path $outputDir | Out-Null

& $compiler /nologo /target:winexe /platform:x64 /optimize+ `
    /out:"$outputDir\SemanticOverlay.exe" `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll `
    /reference:System.Web.Extensions.dll `
    /reference:System.Security.dll `
    /reference:"$naudio" `
    @nativeSources

if ($LASTEXITCODE -ne 0) {
    throw "Native host compilation failed with exit code $LASTEXITCODE."
}
Copy-Item -LiteralPath $naudio -Destination (Join-Path $outputDir "NAudio.dll") -Force

& $compiler /nologo /target:exe /platform:x64 /optimize+ `
    /main:SemanticOverlay.NativeHost.CredentialProtectionTest `
    /out:"$outputDir\CredentialProtectionTest.exe" `
    /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll `
    /reference:System.Security.dll /reference:"$naudio" `
    @nativeSources `
    "$hostDir\diagnostics\CredentialProtectionTest.cs"
if ($LASTEXITCODE -ne 0) { throw "Credential protection diagnostic compilation failed." }

& $compiler /nologo /target:exe /platform:x64 /optimize+ `
    /main:SemanticOverlay.NativeHost.MessageReaderSafetyTest `
    /out:"$outputDir\MessageReaderSafetyTest.exe" `
    /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll `
    /reference:System.Security.dll /reference:"$naudio" `
    @nativeSources `
    "$hostDir\diagnostics\MessageReaderSafetyTest.cs"
if ($LASTEXITCODE -ne 0) { throw "Message reader safety diagnostic compilation failed." }

& $compiler /nologo /target:exe /platform:x64 /optimize+ `
    /main:SemanticOverlay.NativeHost.SelectionLookupInteractionTest `
    /out:"$outputDir\SelectionLookupInteractionTest.exe" `
    /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll `
    /reference:System.Security.dll /reference:"$naudio" `
    @nativeSources `
    "$hostDir\diagnostics\SelectionLookupInteractionTest.cs"
if ($LASTEXITCODE -ne 0) { throw "Selection lookup interaction diagnostic compilation failed." }

& $compiler /nologo /target:exe /platform:x64 /optimize+ `
    /main:SemanticOverlay.NativeHost.CloseoutReliabilityTest `
    /out:"$outputDir\CloseoutReliabilityTest.exe" `
    /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll `
    /reference:System.Security.dll /reference:"$naudio" `
    @nativeSources `
    "$hostDir\diagnostics\CloseoutReliabilityTest.cs"
if ($LASTEXITCODE -ne 0) { throw "Development closeout diagnostic compilation failed." }

& $compiler /nologo /target:exe /platform:x64 /optimize+ `
    /main:SemanticOverlay.NativeHost.TrayMenuTest `
    /out:"$outputDir\TrayMenuTest.exe" `
    /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll `
    /reference:System.Security.dll /reference:"$naudio" `
    @nativeSources `
    "$hostDir\diagnostics\TrayMenuTest.cs"
if ($LASTEXITCODE -ne 0) { throw "Tray menu diagnostic compilation failed." }

& $compiler /nologo /target:winexe /platform:x64 /optimize+ `
    /out:"$outputDir\FollowTarget.exe" `
    /reference:System.dll `
    /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll `
    "$hostDir\diagnostics\FollowTarget.cs"

if ($LASTEXITCODE -ne 0) {
    throw "Follow target compilation failed with exit code $LASTEXITCODE."
}

& $compiler /nologo /target:winexe /platform:x64 /optimize+ `
    /out:"$outputDir\CaptionTarget.exe" `
    /reference:System.dll `
    /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll `
    "$hostDir\diagnostics\CaptionTarget.cs"

if ($LASTEXITCODE -ne 0) {
    throw "Caption target compilation failed with exit code $LASTEXITCODE."
}

& $compiler /nologo /target:winexe /platform:x64 /optimize+ `
    /main:SemanticOverlay.NativeHost.CaptionHost `
    /out:"$outputDir\CaptionHost.exe" `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll `
    /reference:System.Web.Extensions.dll `
    /reference:System.Security.dll `
    /reference:"$naudio" `
    @nativeSources `
    "$hostDir\diagnostics\CaptionHost.cs"

if ($LASTEXITCODE -ne 0) {
    throw "Caption host compilation failed with exit code $LASTEXITCODE."
}

& $compiler /nologo /target:exe /platform:x64 /optimize+ `
    /out:"$outputDir\AudioSegmentationTest.exe" `
    /reference:System.dll `
    /reference:"$naudio" `
    "$hostDir\AudioCapture.cs" `
    "$hostDir\ProcessLoopbackAudioClient.cs" `
    "$hostDir\diagnostics\AudioSegmentationTest.cs"

if ($LASTEXITCODE -ne 0) {
    throw "Audio segmentation test compilation failed with exit code $LASTEXITCODE."
}

& $compiler /nologo /target:exe /platform:x64 /optimize+ `
    /out:"$outputDir\ProcessLoopbackTest.exe" `
    /reference:System.dll `
    /reference:"$naudio" `
    "$hostDir\AudioCapture.cs" `
    "$hostDir\ProcessLoopbackAudioClient.cs" `
    "$hostDir\diagnostics\ProcessLoopbackTest.cs"

if ($LASTEXITCODE -ne 0) {
    throw "Process loopback test compilation failed with exit code $LASTEXITCODE."
}

& $compiler /nologo /target:exe /platform:x64 /optimize+ `
    /main:SemanticOverlay.NativeHost.CaptionTextTest `
    /out:"$outputDir\CaptionTextTest.exe" `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll `
    /reference:System.Web.Extensions.dll `
    /reference:System.Security.dll `
    /reference:"$naudio" `
    @nativeSources `
    "$hostDir\diagnostics\CaptionTextTest.cs"

if ($LASTEXITCODE -ne 0) {
    throw "Caption text test compilation failed with exit code $LASTEXITCODE."
}

& $compiler /nologo /target:exe /platform:x64 /optimize+ `
    /main:SemanticOverlay.NativeHost.LocalReminderTest `
    /out:"$outputDir\LocalReminderTest.exe" `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll `
    /reference:System.Web.Extensions.dll `
    /reference:System.Security.dll `
    /reference:"$naudio" `
    @nativeSources `
    "$hostDir\diagnostics\LocalReminderTest.cs"

if ($LASTEXITCODE -ne 0) {
    throw "Local reminder test compilation failed with exit code $LASTEXITCODE."
}

& $compiler /nologo /target:exe /platform:x64 /optimize+ `
    /main:SemanticOverlay.NativeHost.CalendarFlowTest `
    /out:"$outputDir\CalendarFlowTest.exe" `
    /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll `
    /reference:System.Security.dll /reference:"$naudio" `
    @nativeSources "$hostDir\diagnostics\CalendarFlowTest.cs"
if ($LASTEXITCODE -ne 0) { throw "Calendar flow diagnostic compilation failed." }

& $compiler /nologo /target:exe /platform:x64 /optimize+ `
    /main:SemanticOverlay.NativeHost.CompactWorkflowTest `
    /out:"$outputDir\CompactWorkflowTest.exe" `
    /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll `
    /reference:System.Security.dll /reference:"$naudio" `
    @nativeSources "$hostDir\diagnostics\CompactWorkflowTest.cs"
if ($LASTEXITCODE -ne 0) { throw "Compact workflow diagnostic compilation failed." }

& $compiler /nologo /target:exe /platform:x64 /optimize+ `
    /main:SemanticOverlay.NativeHost.RefinementTest `
    /out:"$outputDir\RefinementTest.exe" `
    /reference:System.dll /reference:System.Core.dll `
    /reference:System.Drawing.dll /reference:System.Windows.Forms.dll `
    /reference:System.Web.Extensions.dll `
    /reference:System.Security.dll /reference:"$naudio" `
    @nativeSources `
    "$hostDir\diagnostics\RefinementTest.cs"
if ($LASTEXITCODE -ne 0) { throw "Refinement test compilation failed." }

Write-Host "Built $outputDir\SemanticOverlay.exe"
