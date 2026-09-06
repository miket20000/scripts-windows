[CmdletBinding()]
param(
    [string]$TaskName = "WSL - uruchom aplikacje po zalogowaniu"
)

$ErrorActionPreference = "Stop"

$wsl = "$env:SystemRoot\System32\wsl.exe"
$userId = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
$arguments = "-d Ubuntu -- /home/miket/coding/scripts/start-wsl-gui-apps.sh"
$action = New-ScheduledTaskAction -Execute $wsl -Argument $arguments
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $userId
$trigger.Delay = "PT10S"
$principal = New-ScheduledTaskPrincipal `
    -UserId $userId `
    -LogonType Interactive `
    -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet `
    -StartWhenAvailable `
    -MultipleInstances IgnoreNew `
    -ExecutionTimeLimit (New-TimeSpan -Minutes 5)

Register-ScheduledTask `
    -TaskName $TaskName `
    -Action $action `
    -Trigger $trigger `
    -Principal $principal `
    -Settings $settings `
    -Force | Out-Null

$updated = Get-ScheduledTask -TaskName $TaskName
if ($updated.Actions.Execute -ne $wsl -or
    $updated.Actions.Arguments -ne $arguments -or
    $updated.Triggers.Count -ne 1 -or
    $updated.Triggers[0].CimClass.CimClassName -ne "MSFT_TaskLogonTrigger" -or
    $updated.Triggers[0].UserId -ne $userId -or
    $updated.Triggers[0].Delay -ne "PT10S" -or
    $updated.Principal.LogonType -ne "Interactive" -or
    $updated.Principal.RunLevel -ne "Limited") {
    throw "WSL interactive application task did not persist as expected."
}

Write-Output "Configured task: $($updated.TaskPath)$($updated.TaskName)"
Write-Output "Trigger: interactive logon of $userId after 10 seconds"
Write-Output "Action: $($updated.Actions.Execute) $($updated.Actions.Arguments)"
