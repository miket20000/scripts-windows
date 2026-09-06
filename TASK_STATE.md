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
- Implementation commit `069ac99` is on `origin/main` and was fast-forwarded
  to the devbox worktree.
- The devbox user service and timer are installed. The service completed twice
  with `Result=success`, `ExecMainStatus=0`, and `NOOP`; the timer is enabled,
  active, and waiting on its five-minute schedule.
- The Windows task is registered with an interactive limited principal, logon
  and five-minute triggers, non-overlap, network requirement, battery support,
  and a five-minute execution limit. Its persisted configuration passed
  readback after account-name comparison was corrected to use the user SID.
- The stale source and worktree paths in
  `codex-telegram-notify/TASK_STATE.md` now point to `scripts-windows`.
- Deployment completion is pending the real Windows scheduler run and final
  cross-host parity check.

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

## Important files

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
- The Windows task must still be started manually after this readback fix is
  committed so that its real scheduler environment and log can be verified.

## START HERE

Commit and push the Windows SID readback correction and this state snapshot.
Run the Windows task through Task Scheduler, verify exit code and log output,
then confirm the devbox timer fast-forwards the correction and both worktrees
remain clean at the same commit. Record the final deployment state.
