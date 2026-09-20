param(
 [Parameter(Mandatory=$true)][string]$HostExecutable,
 [switch]$ExpectProviderFailure
)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class AudioUiNative {
 public delegate bool EnumProc(IntPtr h,IntPtr l);
 [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p,IntPtr l);
 [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h,out uint p);
 [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h,uint m,IntPtr w,IntPtr l);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 public static int Hotkey(int pid,int id){int sent=0;EnumWindows(delegate(IntPtr h,IntPtr l){uint p;GetWindowThreadProcessId(h,out p);if(p==pid&&PostMessage(h,0x0312,(IntPtr)id,IntPtr.Zero))sent++;return true;},IntPtr.Zero);return sent;}
}
'@
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$hostRoot=[IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $HostExecutable) '..\..'))
$log=Join-Path $hostRoot '_native_host.log'
$before=if(Test-Path $log){(Get-Item $log).Length}else{0}
$hostProcess=$null;$targetProcess=$null
try {
 $hostProcess=Start-Process -FilePath $HostExecutable -PassThru
 Start-Sleep -Seconds 2
 $targetProcess=Start-Process -FilePath (Join-Path $root 'native-host\bin\AudioMeetingTarget.exe') `
    -ArgumentList @((Join-Path $root 'tests\ui-speech.wav'),(Join-Path $root 'tests\audio-caption-ui.png')) -PassThru
 $clicked=$false
 for($wait=0;$wait -lt 30 -and $targetProcess.MainWindowHandle -eq 0;$wait++){Start-Sleep -Milliseconds 100;$targetProcess.Refresh()}
 [AudioUiNative]::SetForegroundWindow($targetProcess.MainWindowHandle)|Out-Null
 if([AudioUiNative]::Hotkey($hostProcess.Id,1)-lt 1){throw 'Host hotkey window was not found.'}
 $deadline=(Get-Date).AddSeconds(8)
 while(-not $clicked -and (Get-Date)-lt $deadline){
    $buttons=[System.Windows.Automation.AutomationElement]::RootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Button))
    foreach($button in $buttons){
        if($button.Current.Name -match '^是|Yes'){$button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke();$clicked=$true;break}
    }
    Start-Sleep -Milliseconds 200
 }
 if(-not $clicked){throw 'Audio consent dialog was not observed.'}
 $targetProcess.WaitForExit(25000)|Out-Null
 [AudioUiNative]::Hotkey($hostProcess.Id,2)|Out-Null
 Start-Sleep -Seconds 1
 $stream=[IO.File]::Open($log,'Open','Read','ReadWrite')
 try{$stream.Position=$before;$reader=[IO.StreamReader]::new($stream);$newLog=$reader.ReadToEnd()}finally{if($reader){$reader.Dispose()};$stream.Dispose()}
 if($newLog -match 'Windows OCR scan:'){throw 'Meeting audio session incorrectly invoked screen OCR.'}
 if($newLog -notmatch 'System audio caption capture stopped'){throw 'Stopping did not release audio capture.'}
 if($ExpectProviderFailure){
    $failures=[regex]::Matches($newLog,'Audio caption failure:').Count
    if($failures -ne 1){throw "Expected exactly one provider failure, observed $failures."}
    if($newLog -notmatch '余额不足'){throw 'The provider failure was not shown as insufficient balance.'}
    [pscustomobject]@{passed=$true;consent_observed=$clicked;provider_failure='balance';failure_count=$failures;capture_stopped=$true;screen_ocr_calls=0}|ConvertTo-Json
 } else {
    if($newLog -notmatch 'Accepted audio transcript with'){throw 'No audio transcript reached the installed UI.'}
    if($newLog -notmatch 'audio-caption term windows'){throw 'No clickable term windows were rendered.'}
    [pscustomobject]@{passed=$true;consent_observed=$clicked;audio_transcript=$true;clickable_terms=$true;screen_ocr_calls=0;screenshot=(Join-Path $root 'tests\audio-caption-ui.png')}|ConvertTo-Json
 }
} finally {
 foreach($process in @($targetProcess,$hostProcess)){if($process -and -not $process.HasExited){Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue}}
}
