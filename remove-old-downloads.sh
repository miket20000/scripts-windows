#!/usr/bin/env bash
# Delete files and directories older than a chosen number of days from Downloads.
# Default path mirrors the former Windows Scheduled Task through WSL.

set -euo pipefail

readonly DEFAULT_PATH='/mnt/c/Users/mjtar/Downloads'
readonly DEFAULT_OLDER_THAN_DAYS=30

path="$DEFAULT_PATH"
older_than_days="$DEFAULT_OLDER_THAN_DAYS"
recurse=false
dry_run=false

usage() {
    cat <<'EOF'
Usage: remove-old-downloads.sh [OPTIONS]

Delete files and directories older than 30 days from the Windows Downloads folder,
accessed from Ubuntu/WSL.

Options:
  --path PATH             Folder to clean (default: /mnt/c/Users/mjtar/Downloads)
  --older-than-days DAYS  Delete files older than DAYS (default: 30)
  --recurse               Include files and directories in subdirectories
  --dry-run               Show files that would be deleted without deleting them
  -h, --help              Show this help
EOF
}

while (($# > 0)); do
    case "$1" in
        --path)
            (($# >= 2)) || { echo 'Missing value for --path.' >&2; exit 2; }
            path="$2"
            shift 2
            ;;
        --older-than-days)
            (($# >= 2)) || { echo 'Missing value for --older-than-days.' >&2; exit 2; }
            older_than_days="$2"
            shift 2
            ;;
        --recurse)
            recurse=true
            shift
            ;;
        --dry-run)
            dry_run=true
            shift
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            echo "Unknown option: $1" >&2
            usage >&2
            exit 2
            ;;
    esac
done

if [[ ! "$older_than_days" =~ ^[0-9]+$ ]]; then
    echo "--older-than-days must be a non-negative integer: $older_than_days" >&2
    exit 2
fi

if [[ ! -d "$path" ]]; then
    echo "Folder not found: $path" >&2
    exit 1
fi

cutoff_file="$(mktemp)"
trap 'rm -f -- "$cutoff_file"' EXIT
touch --date="$older_than_days days ago" "$cutoff_file"

find_args=("$path")
if [[ "$recurse" == false ]]; then
    find_args+=(-maxdepth 1)
fi
find_args+=(-mindepth 1 -depth \( -type f -o -type d \) ! -newer "$cutoff_file" -print0)

count=0
while IFS= read -r -d '' item; do
    ((count += 1))
    if [[ "$dry_run" == true ]]; then
        printf 'Would delete: %s\n' "$item"
    else
        rm -rf -- "$item"
    fi
done < <(find "${find_args[@]}")

if [[ "$dry_run" == true ]]; then
    printf 'Found %d item(s) older than %d days in %q; no files or directories were deleted.\n' \
        "$count" "$older_than_days" "$path"
else
    printf 'Deleted %d item(s) older than %d days from %q.\n' \
        "$count" "$older_than_days" "$path"
fi
