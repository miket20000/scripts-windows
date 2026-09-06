[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

$matches = @(Get-ScheduledTask | Where-Object { $_.TaskName -like "*WSL*" })
if ($matches.Count -ne 1) {
    throw "Expected one WSL startup task, found $($matches.Count)."
}

$task = $matches[0]
$wsl = "C:\Windows\System32\wsl.exe"
$dueCheck = "$wsl -d Ubuntu -- /home/miket/coding/scripts/codex-weekly-maintenance.sh --is-due"
$startService = "$wsl -d Ubuntu -u root -- systemctl start codex-weekly-maintenance.service"
# `cmd.exe` expands %ERRORLEVEL% while parsing the complete command line,
# before the preceding WSL process exits. Delayed expansion reads the actual
# WSL exit code after it has completed, so exit 10 can reliably start service.
$command = "$dueCheck & set `"rc=!ERRORLEVEL!`" & if !rc! EQU 10 (taskkill.exe /F /IM ChatGPT.exe >nul 2>&1 & taskkill.exe /F /IM codex.exe >nul 2>&1 & $startService) else if !rc! EQU 0 (exit /b 0) else (exit /b !rc!)"
$actionArguments = '/d /v:on /c "' + $command + '"'
$action = New-ScheduledTaskAction -Execute "$env:SystemRoot\System32\cmd.exe" -Argument $actionArguments

Set-ScheduledTask -TaskName $task.TaskName -TaskPath $task.TaskPath -Action $action | Out-Null

$updated = @(Get-ScheduledTask | Where-Object { $_.TaskName -eq $task.TaskName })[0]
if ($updated.Actions.Execute -ne "$env:SystemRoot\System32\cmd.exe" -or
    $updated.Actions.Arguments -notlike "*/v:on*" -or
    $updated.Actions.Arguments -notlike "*rc=!ERRORLEVEL!*" -or
    $updated.Actions.Arguments -notlike "*codex-weekly-maintenance.service*") {
    throw "WSL startup task action did not persist as expected."
}

Write-Output "Updated task: $($updated.TaskPath)$($updated.TaskName)"
Write-Output "Action: $($updated.Actions.Execute) $($updated.Actions.Arguments)"
