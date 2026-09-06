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

- Before implementation, both worktrees were clean on commit
  `ea5beaeac31261fe7f14a93942e750450caa908d` and tracked the same
  `origin/main`.
- The Windows host has Git 2.55.0 and uses Task Scheduler.
- The devbox has Git 2.53.0, a running user systemd manager, and `Linger=yes`.
- Both executors, the Windows task configurator, the devbox systemd units,
  line-ending policy, and operating documentation are implemented locally.
- The stale source and worktree paths in
  `codex-telegram-notify/TASK_STATE.md` now point to `scripts-windows`.
- Deployment is pending the candidate commit and push.

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
- `BLOCKED` until the candidate is present on the devbox: `systemd-analyze`
  cannot validate an `ExecStart` executable that has not been pulled yet.

## Planned files

- `safe-git-sync.ps1` and `safe-git-sync.sh` — fail-latched synchronization
  executors.
- `configure-git-sync-task.ps1` — Windows task registration and readback.
- `systemd/user/scripts-windows-git-sync.service` and `.timer` — devbox user
  units.
- `.gitattributes` — explicit cross-platform line-ending policy.
- `README.md` — operating and recovery instructions.

## Risks and open checks

- A dirty worktree deliberately pauses synchronization until the user resolves
  or commits the changes.
- Concurrent commits on both hosts deliberately latch as divergence and
  require manual reconciliation.
- The Windows task and devbox timer must be installed, started once manually,
  and read back before the deployment is complete.

## START HERE

Review the candidate diff and secrets scan, then commit and push it.
Fast-forward the devbox worktree, validate and install the systemd units, and
configure the Windows task. Run each scheduler once through its real execution
environment and verify the persisted configuration, logs, and matching HEADs
before marking deployment complete.
