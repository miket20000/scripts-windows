#!/usr/bin/env bash

set -euo pipefail

readonly ENDPOINT='https://auth.gp.edu.pl/v1/credentials'
readonly -a APPLICATIONS=(
    'ai-audio'
    'ai-imaging'
    'ai-music'
    'ai-text'
    'ai-video'
)

usage() {
    cat <<'EOF'
Użycie: create-ai-credential.sh

Interaktywnie tworzy klucz dostępu dla jednej z aplikacji AI GP.
Wymaga dwóch kroków: wyboru aplikacji i podania tokenu Bearer.
EOF
}

require_command() {
    local command_name="$1"

    if ! command -v "$command_name" >/dev/null 2>&1; then
        printf 'Brak wymaganego polecenia: %s\n' "$command_name" >&2
        return 1
    fi
}

choose_application() {
    local index selection

    printf 'Krok 1/2 — wybór aplikacji\n' >&2
    printf 'Dostępne aplikacje:\n' >&2
    for index in "${!APPLICATIONS[@]}"; do
        printf '  %d) %s\n' "$((index + 1))" "${APPLICATIONS[index]}" >&2
    done

    while true; do
        printf 'Wybierz numer aplikacji: ' >&2
        if ! IFS= read -r selection; then
            printf '\nNie odczytano wyboru aplikacji.\n' >&2
            return 1
        fi

        if [[ "$selection" =~ ^[1-5]$ ]]; then
            printf '%s\n' "${APPLICATIONS[selection - 1]}"
            return 0
        fi

        printf 'Nieprawidłowy wybór. Podaj numer od 1 do 5.\n' >&2
    done
}

read_token() {
    local token

    printf 'Krok 2/2 — token Bearer: ' >&2
    if ! IFS= read -r -s token; then
        printf '\nNie odczytano tokenu Bearer.\n' >&2
        return 1
    fi
    printf '\n' >&2

    if [[ -z "$token" ]]; then
        printf 'Token Bearer nie może być pusty.\n' >&2
        return 1
    fi

    printf '%s' "$token"
}

main() {
    local application auth_token payload

    if (($# > 0)); then
        case "$1" in
            -h|--help)
                if (($# > 1)); then
                    printf 'Opcja %s nie przyjmuje dodatkowych argumentów.\n' "$1" >&2
                    return 64
                fi
                usage
                return 0
                ;;
            *)
                printf 'Nieznana opcja: %s\n' "$1" >&2
                usage >&2
                return 64
                ;;
        esac
    fi

    require_command curl
    require_command jq

    application="$(choose_application)"
    auth_token="$(read_token)"

    payload="$(
        jq -n \
            --arg application "$application" \
            '{
                application: $application,
                action: "create",
                teacher_id: "1024",
                teacher_name: "test_mt",
                teacher_surname: "test_mt",
                timetable_id: "1024",
                timetable_city_name: "test_mt",
                timetable_city_kind: "stationary",
                timetable_country: "PL"
            }'
    )"

    curl --fail-with-body --silent --show-error \
        --request POST "$ENDPOINT" \
        --header "Authorization: Bearer $auth_token" \
        --header 'Content-Type: application/json' \
        --data "$payload" |
        jq -er '
            if .ok == true and (.credential.value | type) == "string" then
                .credential.value
            else
                error(
                    (.error.code // "unknown_error")
                    + ": "
                    + (.error.message // "Brak komunikatu błędu")
                )
            end
        '
}

main "$@"
