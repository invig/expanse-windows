# Runs a program on the VM's desktop, in the signed-in user's session, from an SSH session
# (which can't show windows or touch the desktop itself). It goes through a scheduled task
# with an interactive logon, not elevated, at normal priority.
#
#   desk.ps1 -Exe PATH [-Arguments "..."] [-Name TASK] [-Wait SECONDS]
#
# With -Wait it waits for the program to finish and exits with its exit code.
param(
    [Parameter(Mandatory)][string]$Exe,
    [string]$Arguments = '',
    [string]$Name = 'ExpanseDesk',
    [int]$Wait = 0
)
$ErrorActionPreference = 'Stop'

$user = (Get-CimInstance Win32_ComputerSystem).UserName
if (-not $user) { throw "Nobody is signed in at the VM's desktop." }
$exePath = (Resolve-Path $Exe).Path

$actionArgs = @{ Execute = $exePath; WorkingDirectory = (Split-Path $exePath) }
if ($Arguments) { $actionArgs.Argument = $Arguments }
$action = New-ScheduledTaskAction @actionArgs
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
# Priority 4 is normal; scheduled tasks default to 7 (below normal), which skews timing.
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances Parallel -Priority 4
Register-ScheduledTask -TaskName $Name -Action $action -Principal $principal -Settings $settings -Force | Out-Null
Start-ScheduledTask -TaskName $Name

if ($Wait -gt 0) {
    $deadline = (Get-Date).AddSeconds($Wait)
    Start-Sleep -Milliseconds 300
    while ((Get-ScheduledTask -TaskName $Name).State -eq 'Running') {
        if ((Get-Date) -gt $deadline) { throw "$Name still running after $Wait s" }
        Start-Sleep -Milliseconds 150
    }
    exit (Get-ScheduledTaskInfo -TaskName $Name).LastTaskResult
}
