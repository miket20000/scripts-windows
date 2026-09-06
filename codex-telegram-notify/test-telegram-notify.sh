#!/usr/bin/env bash

set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
source "$script_dir/telegram-notify.sh"

assert_equals() {
    local expected="$1"
    local actual="$2"
    local test_name="$3"

    if [[ "$actual" != "$expected" ]]; then
        printf 'FAIL: %s\nExpected: %q\nActual:   %q\n' "$test_name" "$expected" "$actual" >&2
        exit 1
    fi
}

assert_equals \
    'Przywróciłem poprzednią wersję głównego [TASK_STATE.md](/home/miket/coding/scripts/TASK_STATE.md) — jest identyczna z wersją sprzed mojego snapshotu.' \
    "$(first_paragraph $'Przywróciłem poprzednią wersję głównego [TASK_STATE.md](/home/miket/coding/scripts/TASK_STATE.md) — jest identyczna z wersją sprzed mojego snapshotu.\n\nDodatkowe szczegóły nie powinny trafić do Telegrama.')" \
    'first paragraph only'

assert_equals \
    $'Pierwsza linia\nDruga linia' \
    "$(first_paragraph $'\n\nPierwsza linia\r\nDruga linia\n\nKolejny akapit')" \
    'multiline paragraph without leading whitespace'

assert_equals '' "$(first_paragraph $'\n \n\t')" 'blank summary'

printf 'telegram-notify tests: ok\n'
