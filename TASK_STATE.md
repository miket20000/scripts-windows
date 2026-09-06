# Scripts Windows — TASK STATE

## Goal

Keep the tracked contents of the `main` branch consistent between the Windows
worktree at `C:\Users\mjtar\coding-windows\scripts-windows` and the devbox
worktree at `/home/miket/coding/scripts-windows`, using `origin/main` as the
coordination point.

## Scope and constraints

- Synchronization is limited to committed, Git-tracked content on `main`.
- Both hosts may push a clean, ahead-only `main` branch and fast-forward a
  clean, behind-only `main` branch.
- Automation must never add, commit, stash, rebase, merge divergent history,
  force-push, delete refs, or modify a dirty worktree. Fetches are restricted
  to the configured branch and do not prune other remote-tracking refs.
- Divergence, authentication failures, and failed postconditions must stop the
  run without discarding local data.
- The scheduled executors remain in this repository. The user accepted the
  small risk that a pull could update the running script because these scripts
  are expected to be created once and rarely changed.
- Secrets, credentials, ignored files, and machine-specific runtime state are
  outside synchronization scope.

## Current status

- The Windows host has Git 2.55.0 and uses Task Scheduler.
- The devbox has Git 2.53.0, a running user systemd manager, and `Linger=yes`.
- Relocation commit `90418bb` is on `origin/main` and was fast-forwarded to the
  devbox worktree. The executors, Windows configurator, app README, and
  versioned systemd units now live under `git-sync`; the repository root
  remains their synchronization target.
- The devbox user service was reinstalled from `git-sync/systemd/user`. Its
  action points to `/home/miket/coding/scripts-windows/git-sync/safe-git-sync.sh`,
  it completed with `Result=success`, `ExecMainStatus=0`, and `NOOP`, and the
  timer is enabled, active, and waiting on its five-minute schedule.
- The Windows task is registered with an interactive limited principal, logon
  and five-minute triggers, non-overlap, network requirement, battery support,
  and a five-minute execution limit. Its action points to
  `git-sync\safe-git-sync.ps1` and launches PowerShell with
  `-WindowStyle Hidden`; the task entry itself remains visible in Task
  Scheduler. Persisted configuration passed readback.
- Both schedulers were disabled before changing their paths and re-enabled
  only after their new configurations passed real execution tests.

## Verification

- `PASS` — PowerShell parser for both `.ps1` files, `bash -n`, and
  `git diff --check`.
- `PASS` — non-interactive `git push --dry-run` on Windows and devbox.
- `PASS` — disposable-clone tests for both PowerShell and Bash: aligned no-op,
  ahead-only push, behind-only fast-forward, dirty-worktree pause, non-main
  branch skip, same-host lock contention, and divergence preserving both
  commits.
- `PASS` — Bash executor dry-run against the real devbox worktree.
- Initial test failures were retained and repaired: Windows execution policy,
  PowerShell 5.1 native stderr handling, and ShellCheck `SC1007` warnings.
- `PASS` — `shellcheck` and `systemd-analyze --user verify` on the devbox after
  the executable was present. The only verify warning came from the unrelated
  system unit `/usr/lib/systemd/user/spice-vdagent.service`.
- `PASS` — real `systemd --user` execution with non-interactive SSH and a clean
  aligned postcondition at `069ac99`.
- `PASS` — real Windows Task Scheduler push and no-op runs using the configured
  interactive principal and non-interactive SSH environment.
- `PASS` — automatic devbox timer pull of the Windows-published follow-up
  commit, followed by a clean matching HEAD and a scheduled next run.
- `PASS` — parser and syntax checks after relocation, plus a Windows dry run
  from `git-sync/safe-git-sync.ps1` confirming that its default repository is
  the parent worktree. `PAUSED_DIRTY` was the expected result for the
  uncommitted relocation candidate.
- `PASS` — post-relocation `bash -n`, `shellcheck`, and
  `systemd-analyze --user verify` on devbox. The only verify warning was the
  pre-existing unrelated `spice-vdagent.service` warning.
- `PASS` — post-relocation real devbox systemd service run and Windows Task
  Scheduler run, both returning `NOOP` on clean commit `90418bb`; both periodic
  schedulers were then confirmed enabled.
- `PASS` — Windows task action readback includes `-WindowStyle Hidden`. A real
  scheduler invocation completed with `LastTaskResult=0`; `PAUSED_DIRTY` was
  expected because the window-style change was not yet committed.

## Important files

- `git-sync/safe-git-sync.ps1` and `git-sync/safe-git-sync.sh` — fail-latched
  synchronization executors.
- `git-sync/configure-git-sync-task.ps1` — Windows task registration and
  readback.
- `git-sync/systemd/user/scripts-windows-git-sync.service` and `.timer` —
  devbox user units.
- `.gitattributes` — explicit cross-platform line-ending policy.
- `git-sync/README.md` — operating and recovery instructions.

## Risks and open checks

- A dirty worktree deliberately pauses synchronization until the user resolves
  or commits the changes.
- Concurrent commits on both hosts deliberately latch as divergence and
  require manual reconciliation.
- The periodic Windows trigger has a ten-year repetition duration because the
  ScheduledTasks cmdlets require a bounded duration for this trigger form.
  Re-register the task before that duration expires or when paths change.

## START HERE

For routine health checks, inspect the Windows task `LastTaskResult` and
`%LOCALAPPDATA%\scripts-windows-git-sync\sync.log`, then inspect
`systemctl --user status scripts-windows-git-sync.timer` and the service
journal on devbox. If either executor reports `FAIL_DIVERGED`, reconcile the
two commits manually on one host and push the resolved `main`; never force-push
or delete either local commit as part of automated recovery.
