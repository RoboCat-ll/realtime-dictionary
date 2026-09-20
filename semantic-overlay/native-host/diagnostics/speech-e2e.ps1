param([string]$WavPath = (Join-Path (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path))) 'tests\captured-loopback.wav'))
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path))
if (Get-NetTCPConnection -LocalPort 8877 -State Listen -ErrorAction SilentlyContinue) { throw 'Port 8877 is already in use.' }
$info = [Diagnostics.ProcessStartInfo]::new()
$info.FileName = Join-Path $projectRoot 'runtime\python.exe'
$info.Arguments = '"' + (Join-Path $projectRoot 'server.py') + '"'
$info.WorkingDirectory = $projectRoot
$info.UseShellExecute = $false
$info.CreateNoWindow = $true
$process = [Diagnostics.Process]::Start($info)
try {
    $health = $null
    for ($attempt=0; $attempt -lt 40; $attempt++) {
        try { $health=Invoke-RestMethod http://127.0.0.1:8877/health -TimeoutSec 1; break } catch {}
        Start-Sleep -Milliseconds 250
    }
    if (-not $health) { throw 'Speech test service unavailable.' }
    $session=Invoke-RestMethod http://127.0.0.1:8877/session -TimeoutSec 2
    $body=@{audio_base64=[Convert]::ToBase64String([IO.File]::ReadAllBytes((Resolve-Path $WavPath)))} | ConvertTo-Json -Compress
    $response=Invoke-RestMethod -Method Post http://127.0.0.1:8877/transcribe `
        -Headers @{'X-RealtimeDictionary-Token'=$session.token} -ContentType 'application/json; charset=utf-8' -Body $body -TimeoutSec 30
    if (-not $response.ok -or [String]::IsNullOrWhiteSpace($response.text)) { throw 'No transcript returned.' }
    [pscustomobject]@{passed=$true;source='real WASAPI loopback WAV';model=$response.model;text=$response.text} | ConvertTo-Json
}
finally {
    if ($process -and -not $process.HasExited) { $process.Kill(); $process.WaitForExit(3000) | Out-Null }
    if ($process) { $process.Dispose() }
}
