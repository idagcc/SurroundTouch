[CmdletBinding()]
param([ValidateSet('Stop','Start')][string]$Action, [switch]$Elevated)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Windows.Forms
try {
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    $principal=New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        if ($Elevated) { throw 'Administrator approval was not granted.' }
        $powershellPath=Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
        $arguments='-NoProfile -ExecutionPolicy Bypass -File "'+$PSCommandPath+'" -Action '+$Action+' -Elevated'
        $child=Start-Process -FilePath $powershellPath -ArgumentList $arguments -Verb RunAs -WindowStyle Hidden -Wait -PassThru
        exit $child.ExitCode
    }
    $service=Get-Service -Name 'updd' -ErrorAction Stop
    if ($Action -eq 'Stop') {
        Stop-Service -Name 'updd' -ErrorAction Stop
        $service.WaitForStatus([ServiceProcess.ServiceControllerStatus]::Stopped,[TimeSpan]::FromSeconds(15))
        $message="UPDD is stopped for this Windows session. Its startup setting was not changed.`r`n`r`nOpen SurroundTouch, press Refresh devices, then Identify each physical screen. If no native touch arrives, UPDD's HID filter may still hold the controllers: use the vendor's normal uninstaller and restart Windows.`r`n`r`nStart-UPDD.cmd reverses this service stop. Restarting Windows may start UPDD again."
    } else {
        Start-Service -Name 'updd' -ErrorAction Stop
        $service.WaitForStatus([ServiceProcess.ServiceControllerStatus]::Running,[TimeSpan]::FromSeconds(15))
        $message='UPDD is running again. Stop SurroundTouch correction before using UPDD mapping.'
    }
    [void][Windows.Forms.MessageBox]::Show($message,'SurroundTouch — UPDD service')
} catch {
    [void][Windows.Forms.MessageBox]::Show($_.Exception.Message,'SurroundTouch — UPDD service',[Windows.Forms.MessageBoxButtons]::OK,[Windows.Forms.MessageBoxIcon]::Error)
    exit 1
}
