#!/usr/bin/env bash
# Sends Codex completion notifications through Telegram on Linux/WSL.

set -euo pipefail

mode="auto"
ignore_activity=false
activity_grace_seconds=30
notification_json=""

usage() {
    cat <<'USAGE'
Usage: telegram-notify.sh [--mode auto|completion|approval|test]
                          [--ignore-activity]
                          [--activity-grace-seconds SECONDS] [JSON]

Codex may provide the event payload either as the JSON argument or on standard input.
USAGE
}

while (($#)); do
    case "$1" in
        --mode)
            mode="${2:?Missing value for --mode}"
            shift 2
            ;;
        --ignore-activity)
            ignore_activity=true
            shift
            ;;
        --activity-grace-seconds)
            activity_grace_seconds="${2:?Missing value for --activity-grace-seconds}"
            shift 2
            ;;
        --help|-h)
            usage
            exit 0
            ;;
        --)
            shift
            notification_json="${1:-}"
            break
            ;;
        *)
            if [[ -z "$notification_json" ]]; then
                notification_json="$1"
                shift
            else
                usage >&2
                exit 2
            fi
            ;;
    esac
done

case "$mode" in
    auto|completion|approval|test) ;;
    *)
        printf 'Unsupported mode: %s\n' "$mode" >&2
        exit 2
        ;;
esac

if ! [[ "$activity_grace_seconds" =~ ^[0-9]+$ ]]; then
    printf '--activity-grace-seconds must be a non-negative integer.\n' >&2
    exit 2
fi

if [[ -z "$notification_json" && ! -t 0 ]]; then
    notification_json="$(cat)"
fi
if [[ -z "$notification_json" ]]; then
    notification_json='{}'
fi

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
settings_path="$script_dir/telegram-notify.json"
log_path="$script_dir/telegram-notify.log"

write_log() {
    local message="$1"
    {
        printf '%s %s\n' "$(date --iso-8601=seconds)" "$message"
    } >>"$log_path" 2>/dev/null || true
}

get_windows_idle_seconds() {
    local idle_seconds

    if ! command -v powershell.exe >/dev/null 2>&1; then
        return 1
    fi

    if ! idle_seconds="$(powershell.exe -NoProfile -NonInteractive -Command 'Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class CodexTelegramIdleInput {
    [StructLayout(LayoutKind.Sequential)]
    public struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [DllImport("user32.dll")]
    public static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
    public static long IdleSeconds() {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
        if (!GetLastInputInfo(ref info)) throw new InvalidOperationException("GetLastInputInfo failed.");
        return unchecked((uint)Environment.TickCount - info.dwTime) / 1000;
    }
}
"@; [CodexTelegramIdleInput]::IdleSeconds()' 2>/dev/null | tr -d '\r' | tail -n 1)"; then
        return 1
    fi

    [[ "$idle_seconds" =~ ^[0-9]+$ ]] || return 1
    printf '%s' "$idle_seconds"
}

compact_text() {
    local value="$1"
    local maximum_length="$2"

    value="${value//$'\000'/}"
    value="$(printf '%s' "$value" | sed -E ':a;N;$!ba;s/\n{3,}/\n\n/g' | sed -E 's/^[[:space:]]+|[[:space:]]+$//g')"
    if ((${#value} > maximum_length)); then
        value="${value:0:maximum_length-3}..."
    fi
    printf '%s' "$value"
}

first_paragraph() {
    local value="$1"

    value="${value//$'\000'/}"
    value="${value//$'\r'/}"
    value="$(awk '
        /^[[:space:]]*$/ {
            if (started) exit
            next
        }
        {
            started = 1
            print
        }
    ' <<<"$value")"
    compact_text "$value" 3000
}

if [[ "${BASH_SOURCE[0]}" != "$0" ]]; then
    return 0
fi

payload_value() {
    local expression="$1"
    jq -r "$expression" <<<"$notification_json" 2>/dev/null || true
}

if ! jq -e . >/dev/null 2>&1 <<<"$notification_json"; then
    write_log 'Notification payload is not valid JSON.'
    exit 0
fi

if [[ ! -f "$settings_path" ]]; then
    write_log "Credential file not found: $settings_path"
    exit 0
fi

if ! settings_json="$(jq -e . "$settings_path" 2>/dev/null)"; then
    write_log "Credential file is not valid JSON: $settings_path"
    exit 0
fi

chat_id="$(jq -r '.chatId // empty' <<<"$settings_json")"
bot_token="$(jq -r '.botToken // empty' <<<"$settings_json")"
if [[ -z "$chat_id" || -z "$bot_token" ]]; then
    write_log 'Telegram credentials are incomplete.'
    exit 0
fi

event_type="$(payload_value '.type // .hook_event_name // .event // empty')"
if [[ "$mode" == 'auto' ]]; then
    if [[ "$event_type" == 'agent-turn-complete' || "$event_type" == 'Stop' || "$event_type" == 'stop' ]]; then
        mode='completion'
    else
        mode='approval'
    fi
fi

working_folder="$(payload_value '.cwd // .working_directory // .workingDirectory // empty')"
working_folder="${working_folder:-$(pwd -P)}"

case "$mode" in
    completion)
        if [[ -n "$event_type" && "$event_type" != 'agent-turn-complete' && "$event_type" != 'Stop' && "$event_type" != 'stop' ]]; then
            exit 0
        fi

        # Codex Desktop invokes this WSL runtime through wsl.exe, allowing an
        # explicit host-input check after the quiet period.
        if ! "$ignore_activity" && ((activity_grace_seconds > 0)); then
            sleep "$activity_grace_seconds"
            if ! idle_seconds="$(get_windows_idle_seconds)"; then
                write_log 'Skipped Completion notification because Windows idle time could not be determined.'
                exit 0
            fi
            if ((idle_seconds < activity_grace_seconds)); then
                write_log "Skipped Completion notification because Windows input occurred within the last $activity_grace_seconds seconds."
                exit 0
            fi
        fi

        summary="$(payload_value '."last-assistant-message" // .last_assistant_message // .message // empty')"
        if [[ -z "$summary" ]]; then
            summary='The Codex turn completed.'
        elif ! jq -e '(."last-assistant-message" // .last_assistant_message // .message) | type == "string"' >/dev/null 2>&1 <<<"$notification_json"; then
            summary="$(jq -c '."last-assistant-message" // .last_assistant_message // .message' <<<"$notification_json")"
        fi
        summary="$(first_paragraph "$summary")"
        summary="${summary:-The Codex turn completed.}"
        message="Codex task finished
Folder: $working_folder

$summary"
        ;;
    approval)
        write_log 'Skipped Approval notification because approval alerts are disabled.'
        exit 0
        ;;
    test)
        message="Codex Telegram notifier test
Folder: $working_folder
Time: $(date '+%Y-%m-%d %H:%M:%S')"
        ;;
esac

message="$(compact_text "$message" 4000)"
if curl --fail --silent --show-error --max-time 15 \
    --data-urlencode "chat_id=$chat_id" \
    --data-urlencode "text=$message" \
    --data-urlencode 'disable_web_page_preview=true' \
    "https://api.telegram.org/bot${bot_token}/sendMessage" >/dev/null; then
    write_log "Sent $mode notification."
else
    write_log "Notification failed while sending $mode notification."
fi
