# Codex Telegram Notify — TASK STATE

## Goal

Provide Telegram notifications for Codex Desktop sessions using Ubuntu on WSL.
Notifications cover completed Codex turns only. The runtime, credentials, and
Telegram processing use Linux paths. Codex Desktop reaches the WSL runtime
through its supported `wsl.exe` bridge; the notifier uses the Windows input API
only to apply the required idle-time condition.

## Decisions retained from the Windows implementation

- Send completion notifications to Telegram only after 30 seconds without
  Windows keyboard or mouse activity.
- Never commit a bot token, chat ID, credentials file, runtime state, or log.
- Treat a token previously pasted into chat as exposed; create a fresh token in
  BotFather before configuration.

## Migration decision and consequence

- The user selected a full WSL migration on 2026-08-03.
- The user disabled all approval notifications on 2026-08-04. Codex runs a
  `PermissionRequest` hook before it knows whether an automatic reviewer will
  approve the request, so it cannot reliably identify notifications that need
  a human response.
- Completion notifications use Windows `GetLastInputInfo` through
  `powershell.exe` after the 30-second delay. If Windows was active within that
  interval, or the idle-time API cannot be queried, the notifier skips the
  completion message.
- The previous DPAPI-encrypted credential file under the Windows profile is not
  usable in the Linux runtime and must not be copied. The WSL installer stores a
  new credential file with permissions `600`.

## Current status

- Project source directory: `/home/miket/coding/powershell/codex-telegram-notify`.
- The Git worktree root is `/home/miket/coding/powershell`; this project is a
  subdirectory of that worktree.
- Source migration is complete: `telegram-notify.sh` and
  `setup-telegram-notify.sh` replace the PowerShell scripts. They require Bash,
  `curl`, and `jq`.
- `config.example.toml` preserves the legacy WSL completion command. Desktop
  uses the visible `Stop` lifecycle hook; there is deliberately no
  approval-hook example.
- The source tree has been checked with `bash -n`, `jq empty`, and
  `git diff --check`. The no-payload path was verified to log a missing
  credential file and exit successfully before credentials were configured.
- A timestamped backup of the former Linux `config.toml` and `.bashrc` is stored
  at `/home/miket/.codex/telegram-notify-backups/20260803-wsl-migration`.
- The WSL runtime configuration is installed:
  - `/home/miket/.codex/config.toml` has no legacy `notify` command; completion
    delivery is owned by the `Stop` hook.
  - `/home/miket/.codex/hooks.json` contains a completion-only `Stop` hook and
    no `PermissionRequest` hook.
  - `/home/miket/.bashrc` exports `CODEX_HOME=/home/miket/.codex` for new WSL
    shell sessions.
  - `/home/miket/.codex/telegram-notify/telegram-notify.sh` is installed with
    mode `700`; its credential file was created through the WSL setup flow with
    mode `600`.
- The current Codex desktop-app process still inherited
  `CODEX_HOME=/mnt/c/Users/mjtar/.codex` from Codex Desktop. That value is
  injected by Desktop through `WSLENV`, so it cannot be replaced for a Desktop
  session through `.bashrc`.
- The active Desktop profile is configured as a bridge to WSL:
  - `/mnt/c/Users/mjtar/.codex/hooks.json` contains a visible `Stop` hook that
    calls `wsl.exe -d Ubuntu --` followed by
    `/home/miket/.codex/telegram-notify/telegram-notify.sh --mode completion`;
  - its `PermissionRequest` hook remains absent; the same hook is absent from
    the WSL profile;
  - an explicit `--mode approval` call is fail-closed and logs that approval
    alerts are disabled without contacting Telegram.
- Completion messages contain only the header, working folder, and the first
  non-empty paragraph of the agent summary; later paragraphs are discarded.
- The active WSL notifier was synchronized with this format on 2026-08-10;
  its replaced version is backed up under `telegram-notify-backups`.
- A timestamped backup is created before every active Desktop hook update.
- The Desktop-to-WSL bridge sent a Telegram test message successfully on
  2026-08-04. The test proves the Desktop bridge, Linux runtime, credentials,
  and Telegram connection without exposing credential values.
- The Windows idle-time API was verified through the WSL bridge on 2026-08-04.
  It returned a numeric value; the runtime script passed `bash -n` validation.
- A backup of both profiles' hook files before approval removal is stored at
  `/mnt/c/Users/mjtar/.codex/telegram-notify-backups/20260804-103643-approval-disabled`.

## Required next steps

1. Restart Codex Desktop and trust the new `Stop` hook when prompted.
2. Confirm that no approval notification is sent for an automatically approved
   action, then leave the computer untouched for 30 seconds after a task
   completes and confirm the completion notification. Inspect the log with
   `tail -n 20 /home/miket/.codex/telegram-notify/telegram-notify.log`.
3. After a Codex Desktop update, recheck that the `Stop` hook remains listed in
   Settings and that its Windows WSL bridge command is intact.

## Important files

- Runtime notifier source: `telegram-notify.sh`
- Installer: `setup-telegram-notify.sh`
- Global notification example: `config.example.toml`
- WSL Codex configuration: `/home/miket/.codex/config.toml`
- Runtime state and logs (not versioned): `/home/miket/.codex/telegram-notify/`
- Active Codex Desktop configuration:
  `/mnt/c/Users/mjtar/.codex/hooks.json`

## Maintenance

Keep this file current as work progresses: replace obsolete status and next-step
details rather than appending a history log. Never add tokens, chat IDs, or
credential blobs.

## START HERE

Restart Codex Desktop, trust the visible `Stop` hook, then confirm that
approvals stay silent and completions arrive only after 30 seconds without
Windows input. Do not replace the Desktop bridge with the legacy PowerShell
notifier.
