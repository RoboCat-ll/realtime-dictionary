param(
    [Parameter(Mandatory = $true)]
    [string]$KeyFile,
    [string]$BaseUrl = "https://api.siliconflow.cn/v1",
    [string]$Model = "Qwen/Qwen2.5-7B-Instruct",
    [string]$Sample = "We will meet the OneAPI team at 14:00 on September 3 for a bootcamp about Kubernetes rollback strategy.",
    [string]$Term = "bootcamp",
    [switch]$Save,
    [switch]$LiveModelTest
)

$ErrorActionPreference = "Stop"
if (-not $LiveModelTest) { throw 'Explicit -LiveModelTest is required; this diagnostic can issue three model requests.' }
if ($Save) { throw 'This diagnostic no longer writes credentials. Use the tray configuration to save them with Windows encryption.' }
Get-Content -LiteralPath 'C:\Users\17863\AGENTS.md' | Select-String -Pattern '硅基流动费用约束|硅基流动仅允许|调用前核对|不得因免费'

$projectRoot = Split-Path -Parent (
    Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path))
$python = Join-Path $projectRoot "runtime\python.exe"
$server = Join-Path $projectRoot "server.py"
$raw = (Get-Content -LiteralPath $KeyFile -Raw).Trim()
$match = [regex]::Match($raw, 'sk-[A-Za-z0-9_-]{20,}')
if (-not $match.Success) {
    throw "The key file does not contain a usable API key."
}
$secret = $match.Value

$info = [Diagnostics.ProcessStartInfo]::new()
$info.FileName = $python
$info.Arguments = '"' + $server + '"'
$info.WorkingDirectory = $projectRoot
$info.UseShellExecute = $false
$info.CreateNoWindow = $true
$info.EnvironmentVariables["SILICONFLOW_API_KEY"] = $secret
$info.EnvironmentVariables["OPENAI_BASE_URL"] = $BaseUrl
$info.EnvironmentVariables["OPENAI_MODEL"] = $Model
$process = [Diagnostics.Process]::Start($info)

try {
    $deadline = (Get-Date).AddSeconds(12)
    do {
        Start-Sleep -Milliseconds 250
        try {
            $health = Invoke-RestMethod -Uri "http://127.0.0.1:8877/health" -TimeoutSec 1
        }
        catch {
            $health = $null
        }
    } while (-not $health -and (Get-Date) -lt $deadline)
    if (-not $health) {
        throw "The temporary service did not become healthy."
    }

    $session = Invoke-RestMethod -Uri "http://127.0.0.1:8877/session" -TimeoutSec 2
    $headers = @{ "X-RealtimeDictionary-Token" = $session.token }
    $analysisBody = @{
        text = $Sample
        mode = "model"
        difficulty = "standard"
    } | ConvertTo-Json -Compress
    $analysis = Invoke-RestMethod -Method Post `
        -Uri "http://127.0.0.1:8877/analyze" `
        -Headers $headers `
        -ContentType "application/json; charset=utf-8" `
        -Body $analysisBody `
        -TimeoutSec 25
    $lookupBody = @{
        term = $Term
        context = $Sample
    } | ConvertTo-Json -Compress
    $lookup = Invoke-RestMethod -Method Post `
        -Uri "http://127.0.0.1:8877/lookup" `
        -Headers $headers `
        -ContentType "application/json; charset=utf-8" `
        -Body $lookupBody `
        -TimeoutSec 25

    $saved = $false


    [pscustomobject]@{
        health_ok = $health.ok
        model = $health.model
        base_url = $health.base_url
        analysis_mode = $analysis.analysis_mode
        entity_terms = @($analysis.entities | ForEach-Object { $_.text })
        action_count = @($analysis.actions).Count
        lookup_mode = $lookup.lookup_mode
        explanation = $lookup.explanation
        config_saved = $saved
    } | ConvertTo-Json -Depth 5
}
finally {
    $secret = $null
    if ($process -and -not $process.HasExited) {
        $process.Kill()
        $process.WaitForExit(3000) | Out-Null
    }
    if ($process) {
        $process.Dispose()
    }
}
