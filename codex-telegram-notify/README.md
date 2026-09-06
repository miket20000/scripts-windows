# Codex Telegram Notify

Sends Telegram messages when Codex needs attention.

## Notifications

- Completed Codex turns.
- Completed Codex turns only.

Completed Codex turns wait for 30 seconds. They are sent only when the Windows
host has had no keyboard or mouse input during that interval.

Each completion message contains the header, working folder, and only the first
non-empty paragraph of the agent summary. Any later paragraphs are omitted.

Codex user-input questions are not included because Codex does not expose a
reliable lifecycle event for them.

## Source Files

- `telegram-notify.sh` - completion notifier for Linux/WSL.
- `setup-telegram-notify.sh` - installs the runtime script and securely
  configures Telegram credentials.
- `config.example.toml` - legacy global Codex `notify` configuration example.
- `hooks.example.json` - current completion-only Codex lifecycle hook.

Runtime files are stored in:

```text
/home/miket/.codex/telegram-notify
```

That folder contains only the runtime script, credentials protected with file
permissions `600`,
deduplication state, and log. Codex discovery settings remain in the required
global files `/home/miket/.codex/config.toml` and
`/home/miket/.codex/hooks.json`.

## Install or Reconfigure

From this source folder, run:

```bash
./setup-telegram-notify.sh
```

The installer targets `/home/miket/.codex` by default. Use `--codex-home PATH`
only when Codex is intentionally launched with a different Linux home.

The script copies the notifier into the runtime folder. The bot token is read
without echoing and saved in a JSON file readable only by the current Linux
user. Do not copy this file between users or systems.

## Test

Send a test message:

```bash
~/.codex/telegram-notify/telegram-notify.sh --mode test --ignore-activity
```

Normal completion messages are sent according to the rules above.

Check summary trimming without sending Telegram messages:

```bash
bash test-telegram-notify.sh
```

## Troubleshooting

Review:

```bash
tail -n 20 ~/.codex/telegram-notify/telegram-notify.log
```

Restart Codex Desktop after changing its `config.toml` or `hooks.json`.

## Requirements

- Bash, `curl`, and `jq` in the WSL distribution.
- Windows PowerShell reachable as `powershell.exe` through WSL interop for the
  30-second host-idle check.
- Codex launched with `CODEX_HOME=/home/miket/.codex`. The source migration does
  not reuse the mounted Windows profile or its DPAPI credential file.

## Codex Desktop with WSL

Codex Desktop can inject its own Windows `CODEX_HOME` into WSL. In that mode,
the active Desktop profile remains under `/mnt/c/Users/mjtar/.codex`, so a
Linux-only `config.toml` is not loaded. Configure the visible Desktop `Stop`
hook in `/mnt/c/Users/mjtar/.codex/hooks.json` to start the WSL notifier:

```json
{
  "hooks": {
    "Stop": [{
      "matcher": "*",
      "hooks": [{
        "type": "command",
        "command": "/home/miket/.codex/telegram-notify/telegram-notify.sh --mode completion",
        "commandWindows": "\"C:\\\\Windows\\\\System32\\\\wsl.exe\" -d Ubuntu -- /home/miket/.codex/telegram-notify/telegram-notify.sh --mode completion",
        "timeout": 50,
        "statusMessage": "Sending Telegram completion notification"
      }]
    }]
  }
}
```

The notifier recognizes both the current `Stop` lifecycle payload and the
legacy `agent-turn-complete` notification payload. Do not configure a
`PermissionRequest` hook for this notifier. Codex invokes
that event before it knows whether an automatic reviewer will approve the
request, so it cannot reliably represent a user action. This bridge retains
Desktop completion notifications while the notifier, credential file, and all
Telegram processing run in WSL.

The WSL notifier checks Windows `GetLastInputInfo` after its 30-second wait. If
the Windows idle time is below the configured threshold, or the idle-time API
cannot be read, it skips the completion notification.
