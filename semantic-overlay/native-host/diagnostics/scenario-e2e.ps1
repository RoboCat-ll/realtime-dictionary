param([switch]$AllowLocalFallback)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path))
if (Get-NetTCPConnection -LocalPort 8877 -State Listen -ErrorAction SilentlyContinue) {
    throw 'Port 8877 is already in use; stop the dictionary through its tray before testing.'
}
$info = [Diagnostics.ProcessStartInfo]::new()
$info.FileName = Join-Path $projectRoot 'runtime\python.exe'
$info.Arguments = '"' + (Join-Path $projectRoot 'server.py') + '"'
$info.WorkingDirectory = $projectRoot
$info.UseShellExecute = $false
$info.CreateNoWindow = $true
$process = [Diagnostics.Process]::Start($info)
try {
    $health = $null
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        if ($process.HasExited) { throw 'Test service exited.' }
        try { $health = Invoke-RestMethod http://127.0.0.1:8877/health -TimeoutSec 1; break } catch {}
        Start-Sleep -Milliseconds 250
    }
    if (-not $health) { throw 'Test service unavailable.' }
    $session = Invoke-RestMethod http://127.0.0.1:8877/session -TimeoutSec 2
    $headers = @{ 'X-RealtimeDictionary-Token' = $session.token }
    function Request-Scenario([string]$path, $payload) {
        Invoke-RestMethod -Method Post -Uri ('http://127.0.0.1:8877' + $path) -Headers $headers `
            -ContentType 'application/json; charset=utf-8' -Body ($payload | ConvertTo-Json -Depth 8 -Compress) -TimeoutSec 25
    }
    function Assert-Scenario($condition, [string]$message) { if (-not $condition) { throw $message } }
    $sample = '我们9月3号下午14:00和OneAPI的同事约一个bootcamp讨论会啊'
    $analysis = Request-Scenario '/analyze' @{ text=$sample; mode='model'; difficulty='standard' }
    $terms = @($analysis.entities | ForEach-Object { $_.text })
    Assert-Scenario ($terms -contains 'OneAPI' -and $terms -contains 'bootcamp') 'Missing expected knowledge terms.'
    Assert-Scenario (@($analysis.actions).Count -gt 0) 'Missing schedule candidate.'
    foreach ($entity in $analysis.entities) {
        Assert-Scenario ($sample.Substring($entity.start, $entity.end - $entity.start) -ceq $entity.text) 'Invalid entity span.'
    }
    $lookup = Request-Scenario '/lookup' @{ term='bootcamp'; context=$sample }
    Assert-Scenario ($lookup.explanation -match '[\u4e00-\u9fff]') 'Explanation is not Chinese.'
    if (-not $AllowLocalFallback) {
        Assert-Scenario ($analysis.analysis_mode -eq 'llm' -and $lookup.lookup_mode -eq 'model') 'Model chain fell back locally; this is not a live-model pass.'
    }
    $incomplete = Request-Scenario '/calendar/clarify' @{ time_text='9月3号下午14:00'; supplement='' }
    Assert-Scenario (-not $incomplete.start -and @($incomplete.missing).Count -gt 0) 'Missing date was silently invented.'
    $clarified = Request-Scenario '/calendar/clarify' @{ time_text='9月3号下午14:00'; supplement='2027年，持续1小时，UTC+08:00' }
    Assert-Scenario ($clarified.start -eq '2027-09-03T14:00' -and $clarified.end -eq '2027-09-03T15:00') 'Clarification produced wrong time.'
    $busy = Request-Scenario '/calendar/export' @{ confirmed=$true; title='合成测试：已有会议'; start='2027-09-03T14:30'; end='2027-09-03T15:30'; utc_offset='+08:00' }
    $draft = @{ title='合成测试：OneAPI bootcamp讨论会'; start=$clarified.start; end=$clarified.end; utc_offset=$clarified.utc_offset; calendar_ics=$busy.ics }
    $conflict = Request-Scenario '/calendar/check' $draft
    Assert-Scenario ($conflict.state -eq 'conflict') 'Overlap was not detected.'
    $draft.start='2027-09-03T16:00'; $draft.end='2027-09-03T17:00'
    $clear = Request-Scenario '/calendar/check' $draft
    Assert-Scenario ($clear.state -eq 'clear') 'Free slot did not pass.'
    $denied = Request-Scenario '/calendar/export' $draft
    Assert-Scenario (-not $denied.ok) 'Unconfirmed export was allowed.'
    # Synthetic acceptance event only; never imports into a real calendar or sends invitations.
    $draft.confirmed=$true
    $event = Request-Scenario '/calendar/export' $draft
    Assert-Scenario ($event.ok -and $event.ics.Contains('DTSTART:20270903T080000Z')) 'Confirmed event has wrong UTC time.'
    $draft.calendar_ics=$event.ics
    $duplicate = Request-Scenario '/calendar/check' $draft
    Assert-Scenario ($duplicate.state -eq 'duplicate') 'Duplicate was not detected.'
    [pscustomobject]@{
        passed=$true; tested_at=(Get-Date).ToString('o'); model=$health.model
        analysis_mode=$analysis.analysis_mode; terms=$terms; actions=@($analysis.actions).Count
        lookup_mode=$lookup.lookup_mode; chinese_explanation=$lookup.explanation
        missing_fields_prompted=$true; numeric_timezone_preserves_start=$true
        conflict_detected=$true; free_slot_detected=$true; unconfirmed_export_denied=$true
        confirmed_utc_correct=$true; duplicate_detected=$true; real_calendar_written=$false
    } | ConvertTo-Json -Depth 5
}
finally {
    if ($process -and -not $process.HasExited) { $process.Kill(); $process.WaitForExit(3000) | Out-Null }
    if ($process) { $process.Dispose() }
}
