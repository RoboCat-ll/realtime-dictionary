param([Parameter(Mandatory=$true)][string]$Installer)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$resolved = (Resolve-Path -LiteralPath $Installer).Path
$verification = Start-Process -FilePath $resolved -ArgumentList '/verify' -PassThru -Wait -WindowStyle Hidden
if ($verification.ExitCode -ne 0) { throw 'Embedded installer verification failed.' }
$process = Start-Process -FilePath $resolved -PassThru
$condition = [System.Windows.Automation.PropertyCondition]::new(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, [int]$process.Id)
$installed = $false
$clicked = $false
$deadline = (Get-Date).AddSeconds(45)
while (-not $process.HasExited -and (Get-Date) -lt $deadline) {
    $windows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Children, $condition)
    foreach ($window in $windows) {
        $elements = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)
        foreach ($element in $elements) {
            $name = $element.Current.Name
            if ($name -like '*安装完成。实时字典将启动*') { $installed = $true }
            if ($name -eq '安装' -and -not $clicked) {
                $invoke = $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
                $invoke.Invoke(); $clicked = $true
            }
            if ($installed -and $name -match '^(确定|OK)(\(O\))?$') {
                $invoke = $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
                $invoke.Invoke()
            }
        }
    }
    Start-Sleep -Milliseconds 250
    $process.Refresh()
}
if (-not $installed) { throw 'Install completion was not observed; inspect installer window.' }
if (-not $process.HasExited) { throw 'Installer did not close after success.' }
$target = Join-Path $env:LOCALAPPDATA 'RealtimeDictionary\App'
$manifest = Get-Content (Join-Path $target 'browser-extension\manifest.json') -Raw | ConvertFrom-Json
Start-Sleep -Seconds 2
$hosts = @(Get-CimInstance Win32_Process -Filter "name='SemanticOverlay.exe'" | Where-Object {
    $_.ExecutablePath -eq (Join-Path $target 'native-host\bin\SemanticOverlay.exe')
})
if ($hosts.Count -ne 1) { throw 'Installed host count is not one.' }
[pscustomobject]@{ installed=$true; version=$manifest.version; host_count=$hosts.Count; path=$target } | ConvertTo-Json
