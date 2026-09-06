[CmdletBinding()]
param(
    [string]$RepoPath,
    [string]$Branch = "main",
    [string]$Remote = "origin",
    [switch]$DryRun,
    [string]$LogPath,
    [string]$LockPath
)

$ErrorActionPreference = "Stop"
$env:GIT_TERMINAL_PROMPT = "0"
$env:GCM_INTERACTIVE = "Never"
$env:GIT_SSH_COMMAND = "ssh -o BatchMode=yes -o StrictHostKeyChecking=yes -o ConnectTimeout=20 -o ServerAliveInterval=10 -o ServerAliveCountMax=2"
$script:GitExecutable = $null
$script:ResolvedRepoPath = $null

if ([string]::IsNullOrWhiteSpace($RepoPath)) {
    $RepoPath = Split-Path -Parent $PSScriptRoot
}
if ([string]::IsNullOrWhiteSpace($LogPath)) {
    $LogPath = Join-Path $env:LOCALAPPDATA "scripts-windows-git-sync\sync.log"
}
if ([string]::IsNullOrWhiteSpace($LockPath)) {
    $LockPath = Join-Path $env:LOCALAPPDATA "scripts-windows-git-sync\sync.lock"
}

function Write-SyncStatus {
    param(
        [Parameter(Mandatory)]
        [string]$Status,

        [Parameter(Mandatory)]
        [string]$Message
    )

    $singleLineMessage = (($Message -replace "[\r\n]+", " ") -replace "\s+", " ").Trim()
    $line = "{0} host={1} status={2} message={3}" -f `
        (Get-Date).ToString("o"), [Environment]::MachineName, $Status, $singleLineMessage
    Write-Output $line

    try {
        $logDirectory = Split-Path -Parent $LogPath
        if ($logDirectory -and -not (Test-Path -LiteralPath $logDirectory)) {
            New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
        }
        Add-Content -LiteralPath $LogPath -Value $line -Encoding utf8
    }
    catch {
        Write-Warning "Could not append to sync log: $($_.Exception.Message)"
    }
}

function Invoke-GitCapture {
    param(
        [Parameter(Mandatory)]
        [string[]]$GitArguments
    )

    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $output = @(& $script:GitExecutable -C $script:ResolvedRepoPath @GitArguments 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    [pscustomobject]@{
        ExitCode = $exitCode
        Output = @($output | ForEach-Object { $_.ToString() })
    }
}

function Get-FirstOutputLine {
    param([object]$Result)

    if ($Result.Output.Count -eq 0) {
        return "no diagnostic output"
    }
    return $Result.Output[0]
}

$lockStream = $null
$exitCode = 0

try {
    $gitCommand = Get-Command git.exe -ErrorAction Stop
    $script:GitExecutable = $gitCommand.Source
    $script:ResolvedRepoPath = (Resolve-Path -LiteralPath $RepoPath).Path

    $insideWorktree = Invoke-GitCapture -GitArguments @("rev-parse", "--is-inside-work-tree")
    if ($insideWorktree.ExitCode -ne 0 -or ($insideWorktree.Output -join "").Trim() -ne "true") {
        Write-SyncStatus "FAIL_CONFIG" "Not a Git worktree: $script:ResolvedRepoPath"
        exit 4
    }

    $topLevelResult = Invoke-GitCapture -GitArguments @("rev-parse", "--show-toplevel")
    $topLevel = ($topLevelResult.Output -join "").Trim()
    if ($topLevelResult.ExitCode -ne 0 -or
        -not [string]::Equals(
            [System.IO.Path]::GetFullPath($topLevel).TrimEnd("\", "/"),
            [System.IO.Path]::GetFullPath($script:ResolvedRepoPath).TrimEnd("\", "/"),
            [System.StringComparison]::OrdinalIgnoreCase
        )) {
        Write-SyncStatus "FAIL_CONFIG" "RepoPath must be the Git worktree root"
        exit 4
    }

    $shallowResult = Invoke-GitCapture -GitArguments @("rev-parse", "--is-shallow-repository")
    if ($shallowResult.ExitCode -ne 0 -or ($shallowResult.Output -join "").Trim() -eq "true") {
        Write-SyncStatus "FAIL_CONFIG" "Shallow repositories are not supported"
        exit 4
    }

    $sparseResult = Invoke-GitCapture -GitArguments @("config", "--bool", "core.sparseCheckout")
    if ($sparseResult.ExitCode -eq 0 -and ($sparseResult.Output -join "").Trim() -eq "true") {
        Write-SyncStatus "FAIL_CONFIG" "Sparse checkouts are not supported"
        exit 4
    }

    try {
        $lockDirectory = Split-Path -Parent $LockPath
        if ($lockDirectory -and -not (Test-Path -LiteralPath $lockDirectory)) {
            New-Item -ItemType Directory -Path $lockDirectory -Force | Out-Null
        }
        $lockStream = [System.IO.File]::Open(
            $LockPath,
            [System.IO.FileMode]::OpenOrCreate,
            [System.IO.FileAccess]::ReadWrite,
            [System.IO.FileShare]::None
        )
    }
    catch [System.IO.IOException] {
        Write-SyncStatus "SKIPPED_LOCKED" "Another synchronization run holds the repository lock"
        exit 0
    }

    $fetchRefSpec = "+refs/heads/${Branch}:refs/remotes/${Remote}/${Branch}"
    $fetch = Invoke-GitCapture -GitArguments @("fetch", "--no-tags", $Remote, $fetchRefSpec)
    if ($fetch.ExitCode -ne 0) {
        Write-SyncStatus "FAIL_FETCH" (Get-FirstOutputLine $fetch)
        exit 3
    }

    $currentBranchResult = Invoke-GitCapture -GitArguments @("symbolic-ref", "--quiet", "--short", "HEAD")
    if ($currentBranchResult.ExitCode -ne 0) {
        Write-SyncStatus "SKIPPED_BRANCH" "HEAD is detached"
        exit 0
    }

    $currentBranch = ($currentBranchResult.Output -join "").Trim()
    if ($currentBranch -ne $Branch) {
        Write-SyncStatus "SKIPPED_BRANCH" "Current branch is $currentBranch; expected $Branch"
        exit 0
    }

    $upstreamResult = Invoke-GitCapture -GitArguments @(
        "rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}"
    )
    $expectedUpstream = "$Remote/$Branch"
    if ($upstreamResult.ExitCode -ne 0 -or ($upstreamResult.Output -join "").Trim() -ne $expectedUpstream) {
        Write-SyncStatus "FAIL_CONFIG" "Expected upstream $expectedUpstream"
        exit 4
    }

    $inProgressMarkers = @(
        "MERGE_HEAD",
        "REBASE_HEAD",
        "CHERRY_PICK_HEAD",
        "REVERT_HEAD",
        "BISECT_LOG",
        "rebase-merge",
        "rebase-apply",
        "sequencer"
    )
    foreach ($marker in $inProgressMarkers) {
        $markerResult = Invoke-GitCapture -GitArguments @("rev-parse", "--git-path", $marker)
        if ($markerResult.ExitCode -ne 0) {
            Write-SyncStatus "FAIL_STATUS" "Cannot inspect Git operation state"
            exit 4
        }
        $markerPath = ($markerResult.Output -join "").Trim()
        if (-not [System.IO.Path]::IsPathRooted($markerPath)) {
            $markerPath = Join-Path $script:ResolvedRepoPath $markerPath
        }
        if (Test-Path -LiteralPath $markerPath) {
            Write-SyncStatus "FAIL_IN_PROGRESS" "Git operation marker exists: $marker"
            exit 4
        }
    }

    $status = Invoke-GitCapture -GitArguments @("status", "--porcelain=v1", "--untracked-files=all")
    if ($status.ExitCode -ne 0) {
        Write-SyncStatus "FAIL_STATUS" (Get-FirstOutputLine $status)
        exit 4
    }
    if ($status.Output.Count -gt 0) {
        Write-SyncStatus "PAUSED_DIRTY" "Worktree has tracked or untracked changes"
        exit 0
    }

    $remoteRef = "refs/remotes/$Remote/$Branch"
    $remoteRefCheck = Invoke-GitCapture -GitArguments @("rev-parse", "--verify", $remoteRef)
    if ($remoteRefCheck.ExitCode -ne 0) {
        Write-SyncStatus "FAIL_CONFIG" "Remote-tracking ref does not exist: $remoteRef"
        exit 4
    }
    $remoteHeadAtCalculation = ($remoteRefCheck.Output -join "").Trim()

    $localHeadAtCalculationResult = Invoke-GitCapture -GitArguments @("rev-parse", "HEAD")
    if ($localHeadAtCalculationResult.ExitCode -ne 0) {
        Write-SyncStatus "FAIL_STATUS" "Cannot resolve local HEAD"
        exit 4
    }
    $localHeadAtCalculation = ($localHeadAtCalculationResult.Output -join "").Trim()

    $countsResult = Invoke-GitCapture -GitArguments @(
        "rev-list", "--left-right", "--count", "HEAD...$remoteRef"
    )
    if ($countsResult.ExitCode -ne 0) {
        Write-SyncStatus "FAIL_STATUS" (Get-FirstOutputLine $countsResult)
        exit 4
    }

    $counts = (($countsResult.Output -join "").Trim() -split "\s+")
    if ($counts.Count -ne 2) {
        Write-SyncStatus "FAIL_STATUS" "Unexpected ahead/behind output"
        exit 4
    }
    $ahead = [int]$counts[0]
    $behind = [int]$counts[1]

    if ($ahead -gt 0 -and $behind -gt 0) {
        Write-SyncStatus "FAIL_DIVERGED" "Local is ahead by $ahead and behind by $behind commit(s)"
        exit 2
    }

    $preActionStatus = Invoke-GitCapture -GitArguments @(
        "status", "--porcelain=v1", "--untracked-files=all"
    )
    if ($preActionStatus.ExitCode -ne 0) {
        Write-SyncStatus "FAIL_STATUS" (Get-FirstOutputLine $preActionStatus)
        exit 4
    }
    if ($preActionStatus.Output.Count -gt 0) {
        Write-SyncStatus "PAUSED_DIRTY" "Worktree changed while the sync state was being calculated"
        exit 0
    }

    $preActionBranchResult = Invoke-GitCapture -GitArguments @(
        "symbolic-ref", "--quiet", "--short", "HEAD"
    )
    $preActionHeadResult = Invoke-GitCapture -GitArguments @("rev-parse", "HEAD")
    if ($preActionBranchResult.ExitCode -ne 0 -or
        ($preActionBranchResult.Output -join "").Trim() -ne $Branch -or
        $preActionHeadResult.ExitCode -ne 0 -or
        ($preActionHeadResult.Output -join "").Trim() -ne $localHeadAtCalculation) {
        Write-SyncStatus "STATE_CHANGED" "Branch or HEAD changed while the sync state was being calculated"
        exit 0
    }

    $actionStatus = "NOOP"
    if ($behind -gt 0) {
        if ($DryRun) {
            Write-SyncStatus "WOULD_PULL" "Would fast-forward by $behind commit(s)"
            exit 0
        }

        $merge = Invoke-GitCapture -GitArguments @("merge", "--ff-only", $remoteHeadAtCalculation)
        if ($merge.ExitCode -ne 0) {
            Write-SyncStatus "FAIL_PULL" (Get-FirstOutputLine $merge)
            exit 4
        }
        $actionStatus = "PULLED"
    }
    elseif ($ahead -gt 0) {
        $pushArguments = @("push", "--porcelain")
        if ($DryRun) {
            $pushArguments += "--dry-run"
        }
        $pushArguments += @($Remote, "${localHeadAtCalculation}:refs/heads/$Branch")
        $push = Invoke-GitCapture -GitArguments $pushArguments
        if ($push.ExitCode -ne 0) {
            $failureStatus = if ($DryRun) { "FAIL_PUSH_DRY_RUN" } else { "FAIL_PUSH" }
            Write-SyncStatus $failureStatus (Get-FirstOutputLine $push)
            exit 3
        }
        if ($DryRun) {
            Write-SyncStatus "WOULD_PUSH" "Would push $ahead commit(s)"
            exit 0
        }
        $actionStatus = "PUSHED"
    }
    elseif ($DryRun) {
        Write-SyncStatus "NOOP_DRY_RUN" "Local and remote are aligned"
        exit 0
    }

    $postFetch = Invoke-GitCapture -GitArguments @("fetch", "--no-tags", $Remote, $fetchRefSpec)
    if ($postFetch.ExitCode -ne 0) {
        Write-SyncStatus "POSTCONDITION_UNVERIFIED" "Post-action fetch failed; no rollback was attempted"
        exit 4
    }

    $localHeadResult = Invoke-GitCapture -GitArguments @("rev-parse", "HEAD")
    $remoteHeadResult = Invoke-GitCapture -GitArguments @("rev-parse", $remoteRef)
    $postStatus = Invoke-GitCapture -GitArguments @("status", "--porcelain=v1", "--untracked-files=all")
    $localHead = ($localHeadResult.Output -join "").Trim()
    $remoteHead = ($remoteHeadResult.Output -join "").Trim()

    if ($localHeadResult.ExitCode -ne 0 -or
        $remoteHeadResult.ExitCode -ne 0 -or
        $postStatus.ExitCode -ne 0 -or
        $postStatus.Output.Count -gt 0) {
        Write-SyncStatus "FAIL_POSTCONDITION" "Local main is not clean and aligned with $expectedUpstream"
        exit 4
    }

    if ($localHead -ne $remoteHead) {
        $postCountsResult = Invoke-GitCapture -GitArguments @(
            "rev-list", "--left-right", "--count", "HEAD...$remoteRef"
        )
        $postCounts = (($postCountsResult.Output -join "").Trim() -split "\s+")
        if ($postCountsResult.ExitCode -eq 0 -and
            $postCounts.Count -eq 2 -and
            [int]$postCounts[0] -eq 0 -and
            [int]$postCounts[1] -gt 0) {
            Write-SyncStatus "PENDING_REMOTE_MOVED" "Remote advanced after this run; the next run will fast-forward"
            exit 0
        }

        Write-SyncStatus "FAIL_POSTCONDITION" "Remote changed incompatibly during synchronization"
        exit 4
    }

    Write-SyncStatus $actionStatus "Local main is clean and aligned with $expectedUpstream at $localHead"
}
catch {
    Write-SyncStatus "FAIL_RUNTIME" "$($_.Exception.Message) at line $($_.InvocationInfo.ScriptLineNumber)"
    $exitCode = 4
}
finally {
    if ($null -ne $lockStream) {
        $lockStream.Dispose()
    }
}

exit $exitCode
