#!/usr/bin/env bash
# Installs the WSL notifier in the active Codex home and creates its credentials.

set -euo pipefail

chat_id=""
codex_home="$HOME/.codex"
skip_test=false

usage() {
    cat <<'USAGE'
Usage: setup-telegram-notify.sh [--chat-id CHAT_ID] [--codex-home PATH] [--skip-test]
USAGE
}

while (($#)); do
    case "$1" in
        --chat-id)
            chat_id="${2:?Missing value for --chat-id}"
            shift 2
            ;;
        --codex-home)
            codex_home="${2:?Missing value for --codex-home}"
            shift 2
            ;;
        --skip-test)
            skip_test=true
            shift
            ;;
        --help|-h)
            usage
            exit 0
            ;;
        *)
            usage >&2
            exit 2
            ;;
    esac
done

for dependency in jq install; do
    if ! command -v "$dependency" >/dev/null 2>&1; then
        printf 'Required command is unavailable: %s\n' "$dependency" >&2
        exit 1
    fi
done

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
app_root="$codex_home/telegram-notify"
settings_path="$app_root/telegram-notify.json"
notifier_path="$app_root/telegram-notify.sh"

if [[ -z "$chat_id" ]]; then
    read -r -p 'Telegram chat ID: ' chat_id
fi
if [[ -z "$chat_id" ]]; then
    printf 'Telegram chat ID cannot be empty.\n' >&2
    exit 1
fi

read -r -s -p 'Telegram bot token: ' bot_token
printf '\n'
if [[ -z "$bot_token" ]]; then
    printf 'Telegram bot token cannot be empty.\n' >&2
    exit 1
fi

install -d -m 700 "$app_root"
install -m 700 "$script_dir/telegram-notify.sh" "$notifier_path"

settings_tmp="$(mktemp "$app_root/telegram-notify.json.XXXXXX")"
trap 'rm -f -- "$settings_tmp"' EXIT
umask 077
jq -n --arg chatId "$chat_id" --arg botToken "$bot_token" \
    '{chatId: $chatId, botToken: $botToken}' >"$settings_tmp"
install -m 600 "$settings_tmp" "$settings_path"

printf 'Telegram credentials saved to %s (permissions: 600).\n' "$settings_path"

if ! "$skip_test"; then
    printf 'Sending a test message...\n'
    "$notifier_path" --mode test --ignore-activity
    printf 'Check Telegram and %s/telegram-notify.log for the result.\n' "$app_root"
fi
