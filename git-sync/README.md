# Safe Git synchronization

`safe-git-sync.ps1` and `safe-git-sync.sh` keep committed content on `main`
aligned through `origin/main`. Both executors use the same fail-latched policy:

- a clean, behind-only branch is fast-forwarded;
- a clean, ahead-only branch is pushed without force;
- a dirty worktree is reported as `PAUSED_DIRTY` and left unchanged;
- divergent history is reported as `FAIL_DIVERGED` and left unchanged;
- detached HEAD and non-`main` branches are skipped;
- no run adds, commits, stashes, rebases, force-pushes, or deletes refs.

Windows dry run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\safe-git-sync.ps1 -DryRun
```

Register or refresh the Windows task:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\configure-git-sync-task.ps1
```

The task runs 30 seconds after interactive logon and then every five minutes.
Its log is stored under `%LOCALAPPDATA%\scripts-windows-git-sync\sync.log`.

Devbox dry run:

```bash
./safe-git-sync.sh --dry-run
```

Run these commands from the `git-sync` directory. Install the devbox user units
from the repository and verify them before enabling the timer:

```bash
install -D -m 0644 systemd/user/scripts-windows-git-sync.service \
  ~/.config/systemd/user/scripts-windows-git-sync.service
install -D -m 0644 systemd/user/scripts-windows-git-sync.timer \
  ~/.config/systemd/user/scripts-windows-git-sync.timer
systemctl --user daemon-reload
systemctl --user start scripts-windows-git-sync.service
systemctl --user status scripts-windows-git-sync.service
systemctl --user enable --now scripts-windows-git-sync.timer
```

Inspect recent devbox runs with:

```bash
journalctl --user -u scripts-windows-git-sync.service -n 20 --no-pager
```

To stop automation without changing either repository, disable the Windows
scheduled task and run:

```bash
systemctl --user disable --now scripts-windows-git-sync.timer
```

Resolve divergence manually on one host, push the reconciled `main`, and leave
the other host clean. The next scheduled run will fast-forward it.
