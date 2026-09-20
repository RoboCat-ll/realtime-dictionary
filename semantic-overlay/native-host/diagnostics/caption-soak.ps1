param([int]$DurationSeconds = 1800, [ValidateSet('CaptionHost.exe','CaptionObserveHost.exe')][string]$HostExecutable='CaptionHost.exe')
$ErrorActionPreference = 'Stop'
Add-Type 'using System; using System.Runtime.InteropServices; public static class CaptionFocusProbe { [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow(); }'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$resultPath = Join-Path $projectRoot ('tests\caption-soak-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.json')
$logPath = Join-Path $projectRoot '_native_host.log'
$before = if (Test-Path $logPath) { (Get-Item $logPath).Length } else { 0 }
$owned = @()
$samples = [Collections.Generic.List[object]]::new()
try {
    if (Get-Process SemanticOverlay,CaptionHost,CaptionObserveHost -ErrorAction SilentlyContinue) { throw 'Close the active dictionary before this test.' }
    $fixture = Start-Process -FilePath (Join-Path $projectRoot 'runtime\python.exe') -ArgumentList @((Join-Path $projectRoot 'tests\local_model_fixture.py')) -WorkingDirectory $projectRoot -WindowStyle Hidden -PassThru
    $owned += $fixture
    $env:OPENAI_BASE_URL = 'http://127.0.0.1:18879/v1'
    $env:OPENAI_MODEL = 'local-soak-fixture'
    $env:SILICONFLOW_API_KEY = 'local-fixture-only'
    $env:CAPTION_TEST_DURATION_MS = [string]($DurationSeconds * 1000)
    $env:CAPTION_TEST_STRESS = '1'
    $captionHost = Start-Process -FilePath (Join-Path $projectRoot ('native-host\bin\' + $HostExecutable)) -WorkingDirectory $projectRoot -WindowStyle Hidden -PassThru
    $owned += $captionHost
    Start-Sleep -Seconds 2
    $target = Start-Process -FilePath (Join-Path $projectRoot 'native-host\bin\CaptionTarget.exe') -WorkingDirectory $projectRoot -PassThru
    $owned += $target
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $targetHandle = $target.MainWindowHandle.ToInt64()
    while (-not $target.HasExited -and $watch.Elapsed.TotalSeconds -lt ($DurationSeconds + 30)) {
        $captionHost.Refresh()
        if ($captionHost.HasExited) { throw 'Host exited during soak' }
        if ($watch.Elapsed.TotalSeconds -gt 15 -and $watch.Elapsed.TotalSeconds -lt 22) {
            $target.Refresh()
            $targetHandle = $target.MainWindowHandle.ToInt64()
            $checkStream = [IO.File]::Open($logPath,'Open','Read','ReadWrite')
            try {
                $checkStream.Position = $before
                $checkReader = [IO.StreamReader]::new($checkStream)
                $tail = $checkReader.ReadToEnd()
            } finally { if ($checkReader) { $checkReader.Dispose() }; $checkStream.Dispose() }
            if ($tail -notmatch ('Highlight session started for HWND ' + $targetHandle)) {
                throw 'Target hotkey was not confirmed. This run is NOT an accepted caption soak.'
            }
        }
        $target.Refresh()
        $focused = [CaptionFocusProbe]::GetForegroundWindow() -eq $target.MainWindowHandle
        $samples.Add([pscustomobject]@{seconds=[int]$watch.Elapsed.TotalSeconds;focused=$focused;memory_mb=[math]::Round($captionHost.WorkingSet64/1MB,2);handles=$captionHost.HandleCount;cpu_seconds=$captionHost.TotalProcessorTime.TotalSeconds})
        if ($samples.Count % 12 -eq 0) { Write-Output ('SOAK ' + [int]$watch.Elapsed.TotalSeconds + ' seconds; MB=' + $samples[$samples.Count-1].memory_mb) }
        Start-Sleep -Seconds 5
        $target.Refresh()
    }
    if (-not $target.HasExited) { throw 'Target did not stop on schedule' }
    $stream = [IO.File]::Open($logPath,'Open','Read','ReadWrite')
    try { $stream.Position = $before; $reader = [IO.StreamReader]::new($stream); $newLog = $reader.ReadToEnd() } finally { if($reader){$reader.Dispose()}; $stream.Dispose() }
    $result = [pscustomobject]@{duration_seconds=[int]$watch.Elapsed.TotalSeconds;model='local fixture, not live provider';samples=$samples;scans=[regex]::Matches($newLog,'Windows OCR scan:').Count;failures=[regex]::Matches($newLog,'Scan response generation \d+: failed').Count;commits=[regex]::Matches($newLog,'Committed stable caption').Count;model_requests=[regex]::Matches($newLog,'Started stable caption refinement').Count;accepted=($watch.Elapsed.TotalSeconds -ge $DurationSeconds -and $newLog.Contains('Highlight session started for HWND ' + $targetHandle))}
    $focusRatio = @($samples | Where-Object focused).Count / [double][math]::Max(1, $samples.Count)
    $result | Add-Member -NotePropertyName foreground_sample_ratio -NotePropertyValue $focusRatio
    $result.accepted = $result.accepted -and $focusRatio -ge 0.8 -and $result.scans -ge ($DurationSeconds / 4) -and $result.failures -eq 0
    $result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $resultPath -Encoding utf8
    Write-Output ('SOAK_RESULT ' + $resultPath)
    Write-Output ($result | Select-Object duration_seconds,scans,failures,commits,model_requests | ConvertTo-Json -Compress)
} finally {
    $children = @(Get-CimInstance Win32_Process | Where-Object { $_.ParentProcessId -in @($owned.Id) -and $_.Name -eq 'python.exe' -and $_.CommandLine -like "*$projectRoot*server.py*" })
    foreach($process in $owned) { if(-not $process.HasExited){Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue} }
    foreach($process in $children) { Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue }
}
