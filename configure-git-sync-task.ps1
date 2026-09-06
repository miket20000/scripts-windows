[CmdletBinding()]
param(
    [string]$TaskName = "Scripts Windows - safe Git sync",
    [string]$RepoPath,
    [string]$Branch = "main",
    [string]$Remote = "origin"
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($RepoPath)) {
    $RepoPath = $PSScriptRoot
}
$resolvedRepoPath = (Resolve-Path -LiteralPath $RepoPath).Path
$syncScript = Join-Path $resolvedRepoPath "safe-git-sync.ps1"
if (-not (Test-Path -LiteralPath $syncScript -PathType Leaf)) {
    throw "Sync executor does not exist: $syncScript"
}

$powerShell = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
$logPath = Join-Path $env:LOCALAPPDATA "scripts-windows-git-sync\sync.log"
$arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "{0}" -RepoPath "{1}" -Branch "{2}" -Remote "{3}" -LogPath "{4}"' -f `
    $syncScript, $resolvedRepoPath, $Branch, $Remote, $logPath
$action = New-ScheduledTaskAction -Execute $powerShell -Argument $arguments

$userId = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
$logonTrigger = New-ScheduledTaskTrigger -AtLogOn -User $userId
$logonTrigger.Delay = "PT30S"
$periodicTrigger = New-ScheduledTaskTrigger `
    -Once `
    -At (Get-Date).AddMinutes(1) `
    -RepetitionInterval (New-TimeSpan -Minutes 5) `
    -RepetitionDuration (New-TimeSpan -Days 3650)

$principal = New-ScheduledTaskPrincipal `
    -UserId $userId `
    -LogonType Interactive `
    -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet `
    -StartWhenAvailable `
    -MultipleInstances IgnoreNew `
    -ExecutionTimeLimit (New-TimeSpan -Minutes 5) `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -RunOnlyIfNetworkAvailable

Register-ScheduledTask `
    -TaskName $TaskName `
    -Action $action `
    -Trigger @($logonTrigger, $periodicTrigger) `
    -Principal $principal `
    -Settings $settings `
    -Force | Out-Null

$updated = Get-ScheduledTask -TaskName $TaskName
$updatedLogonTrigger = @($updated.Triggers | Where-Object {
    $_.CimClass.CimClassName -eq "MSFT_TaskLogonTrigger"
})
$updatedTimeTrigger = @($updated.Triggers | Where-Object {
    $_.CimClass.CimClassName -eq "MSFT_TaskTimeTrigger"
})
if ($updated.Actions.Count -ne 1 -or
    $updated.Actions.Execute -ne $powerShell -or
    $updated.Actions.Arguments -ne $arguments -or
    $updated.Triggers.Count -ne 2 -or
    $updatedLogonTrigger.Count -ne 1 -or
    $updatedLogonTrigger[0].UserId -ne $userId -or
    $updatedLogonTrigger[0].Delay -ne "PT30S" -or
    $updatedTimeTrigger.Count -ne 1 -or
    $updatedTimeTrigger[0].Repetition.Interval -ne "PT5M" -or
    $updatedTimeTrigger[0].Repetition.Duration -ne "P3650D" -or
    $updated.Principal.UserId -ne $userId -or
    $updated.Principal.LogonType -ne "Interactive" -or
    $updated.Principal.RunLevel -ne "Limited" -or
    $updated.Settings.MultipleInstances -ne "IgnoreNew" -or
    $updated.Settings.ExecutionTimeLimit -ne "PT5M" -or
    -not $updated.Settings.StartWhenAvailable -or
    -not $updated.Settings.RunOnlyIfNetworkAvailable -or
    $updated.Settings.DisallowStartIfOnBatteries -or
    $updated.Settings.StopIfGoingOnBatteries) {
    throw "Scheduled task configuration did not persist as expected."
}

Write-Output "Configured task: $($updated.TaskPath)$($updated.TaskName)"
Write-Output "Action: $($updated.Actions.Execute) $($updated.Actions.Arguments)"
Write-Output "Triggers: logon after 30 seconds and every 5 minutes"
