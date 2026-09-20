# Native tray launcher. The host starts and owns its local Python helpers.
$ErrorActionPreference = "Stop"
$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$hostDir = Join-Path $projectDir "native-host"
$exe = Join-Path $hostDir "bin\SemanticOverlay.exe"
$source = Join-Path $hostDir "Program.cs"
$audioSource = Join-Path $hostDir "AudioCapture.cs"
$buildSource = Join-Path $hostDir "build.ps1"
$naudioSource = Join-Path $projectDir "vendor\NAudio\NAudio.dll"
$serverSource = Join-Path $projectDir "server.py"
$log = Join-Path $projectDir "_native_host.log"
$logLineCount = if (Test-Path -LiteralPath $log) { @(Get-Content -LiteralPath $log).Count } else { 0 }

function Get-ProjectHost {
    Get-CimInstance Win32_Process -Filter "Name = 'SemanticOverlay.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.ExecutablePath -eq $exe }
}

function Stop-ProjectHost([object]$processInfo) {
    Write-Host "正在停止这个目录中的旧版本（PID $($processInfo.ProcessId)）…"
    Stop-Process -Id ([int]$processInfo.ProcessId) -Force -ErrorAction Stop
    Start-Sleep -Milliseconds 250
}

function Get-ProjectBackend {
    Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
        Where-Object {
            ($_.Name -eq "python.exe" -or $_.Name -eq "pythonw.exe") -and
            $_.CommandLine -and $_.CommandLine.Contains($projectDir) -and
            $_.CommandLine.Contains("server.py")
        }
}

function Stop-ProjectBackends {
    foreach ($backend in @(Get-ProjectBackend)) {
        Write-Host "正在停止这个目录中的旧后端（PID $($backend.ProcessId)）…"
        Stop-Process -Id ([int]$backend.ProcessId) -Force -ErrorAction SilentlyContinue
    }
}

$latestNativeSource = @($source, $audioSource, $buildSource, $naudioSource,
    (Join-Path $hostDir 'ProcessLoopbackAudioClient.cs'), (Join-Path $hostDir 'LocalReminders.cs'),
    (Join-Path $hostDir 'windows_ocr_worker.ps1'), (Join-Path $hostDir 'windows_ocr.ps1')) |
    Where-Object { Test-Path -LiteralPath $_ } |
    ForEach-Object { (Get-Item -LiteralPath $_).LastWriteTime } |
    Sort-Object -Descending | Select-Object -First 1
$needsBuild = -not (Test-Path $exe) -or
    ($latestNativeSource -and $latestNativeSource -gt (Get-Item $exe).LastWriteTime)

$running = @(Get-ProjectHost)
foreach ($processInfo in $running) {
    $started = $processInfo.CreationDate
    $latestSource = @($source, $serverSource, (Join-Path $projectDir 'calendar_export.py'), (Join-Path $projectDir 'outlook_calendar.py')) |
        Where-Object { Test-Path -LiteralPath $_ } |
        ForEach-Object { (Get-Item -LiteralPath $_).LastWriteTime } |
        Sort-Object -Descending | Select-Object -First 1
    if ($needsBuild -or ($started -and ([DateTime]$started -lt $latestSource))) {
        Stop-ProjectHost $processInfo
        Stop-ProjectBackends
    }
}

# A previous crash can leave this project's backend alive after its host is gone.
# Never let a newly built host silently reuse stale server.py code.
if (-not $running -and @(Get-ProjectBackend).Count -gt 0) {
    Stop-ProjectBackends
}

if ($needsBuild) {
    if (-not (Test-Path $source)) { throw "程序文件缺失，请重新解压完整安装包。" }
    & (Join-Path $hostDir "build.ps1")
}

$running = @(Get-ProjectHost)
$startedNew = $false
if (-not $running) {
    $process = Start-Process -FilePath $exe -WorkingDirectory $projectDir -WindowStyle Hidden -PassThru
    Start-Sleep -Milliseconds 900
    if ($process.HasExited) {
        throw "SemanticOverlay.exe exited immediately (code $($process.ExitCode))."
    }
    $startedNew = $true
    Write-Host "实时字典已启动（PID $($process.Id)）。"
}
else {
    Write-Host "实时字典已经在运行（PID $($running[0].ProcessId)）。"
}

$deadline = (Get-Date).AddSeconds(4)
$registered = -not $startedNew
$registrationFailed = $false
while ($startedNew -and (Get-Date) -lt $deadline) {
    if (Test-Path $log) {
        $newLog = @(Get-Content -LiteralPath $log -ErrorAction SilentlyContinue |
            Select-Object -Skip $logLineCount) -join "`n"
        if ($newLog -match "Ctrl\+Alt\+(K|G) registration: failed") {
            $registrationFailed = $true
            break
        }
        if (($newLog -match "Ctrl\+Alt\+K registration: ok") -and
            ($newLog -match "Ctrl\+Alt\+G registration: ok")) {
            $registered = $true
            break
        }
    }
    Start-Sleep -Milliseconds 200
}
if (-not $registered) {
    if ($startedNew -and -not $process.HasExited) { Stop-ProjectHost (Get-ProjectHost | Select-Object -First 1) }
    Stop-ProjectBackends
    if ($registrationFailed) {
        throw "快捷键被其他程序占用，实时字典没有继续后台运行。请查看 _native_host.log 中的 Win32 error。"
    }
    throw "程序已经启动，但快捷键注册未通过。请查看 _native_host.log。"
}

Write-Host "可以使用：Ctrl+Alt+K 识别当前窗口；Ctrl+Alt+G 清除高亮。"
