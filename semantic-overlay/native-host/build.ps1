$ErrorActionPreference = "Stop"

$hostDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$outputDir = Join-Path $hostDir "bin"
$compiler = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$naudio = Join-Path (Split-Path -Parent $hostDir) "vendor\NAudio\NAudio.dll"

if (-not (Test-Path $compiler)) {
    throw "The built-in .NET Framework C# compiler was not found."
}

New-Item -ItemType Directory -Force -Path $outputDir | Out-Null

& $compiler /nologo /target:winexe /platform:x64 /optimize+ `
    /out:"$outputDir\SemanticOverlay.exe" `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll `
    /reference:System.Web.Extensions.dll `
    /reference:"$naudio" `
    "$hostDir\Program.cs" `
    "$hostDir\UsageMetrics.cs" `
    "$hostDir\LocalReminders.cs" `
    "$hostDir\AudioCapture.cs" `
    "$hostDir\ProcessLoopbackAudioClient.cs"

if ($LASTEXITCODE -ne 0) {
    throw "Native host compilation failed with exit code $LASTEXITCODE."
}
Copy-Item -LiteralPath $naudio -Destination (Join-Path $outputDir "NAudio.dll") -Force

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
    /out:"$outputDir\JevE2ETarget.exe" `
    /reference:System.dll `
    /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll `
    "$hostDir\diagnostics\JevE2ETarget.cs"

if ($LASTEXITCODE -ne 0) {
    throw "Jev E2E target compilation failed with exit code $LASTEXITCODE."
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
    /reference:"$naudio" `
    "$hostDir\Program.cs" `
    "$hostDir\UsageMetrics.cs" `
    "$hostDir\LocalReminders.cs" `
    "$hostDir\AudioCapture.cs" `
    "$hostDir\ProcessLoopbackAudioClient.cs" `
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
    /reference:"$naudio" `
    "$hostDir\Program.cs" `
    "$hostDir\UsageMetrics.cs" `
    "$hostDir\LocalReminders.cs" `
    "$hostDir\AudioCapture.cs" `
    "$hostDir\ProcessLoopbackAudioClient.cs" `
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
    /reference:"$naudio" `
    "$hostDir\Program.cs" `
    "$hostDir\UsageMetrics.cs" `
    "$hostDir\LocalReminders.cs" `
    "$hostDir\AudioCapture.cs" `
    "$hostDir\ProcessLoopbackAudioClient.cs" `
    "$hostDir\diagnostics\LocalReminderTest.cs"

if ($LASTEXITCODE -ne 0) {
    throw "Local reminder test compilation failed with exit code $LASTEXITCODE."
}

& $compiler /nologo /target:exe /platform:x64 /optimize+ `
    /main:SemanticOverlay.NativeHost.RefinementTest `
    /out:"$outputDir\RefinementTest.exe" `
    /reference:System.dll /reference:System.Core.dll `
    /reference:System.Drawing.dll /reference:System.Windows.Forms.dll `
    /reference:System.Web.Extensions.dll /reference:"$naudio" `
    "$hostDir\Program.cs" "$hostDir\UsageMetrics.cs" "$hostDir\LocalReminders.cs" `
    "$hostDir\AudioCapture.cs" "$hostDir\ProcessLoopbackAudioClient.cs" `
    "$hostDir\diagnostics\RefinementTest.cs"
if ($LASTEXITCODE -ne 0) { throw "Refinement test compilation failed." }

Write-Host "Built $outputDir\SemanticOverlay.exe"
