#!/usr/bin/env bash

set -u
export GIT_TERMINAL_PROMPT=0
export GCM_INTERACTIVE=Never
export GIT_SSH_COMMAND='ssh -o BatchMode=yes -o StrictHostKeyChecking=yes -o ConnectTimeout=20 -o ServerAliveInterval=10 -o ServerAliveCountMax=2'

script_dir=$(CDPATH='' cd -- "$(dirname -- "$0")" && pwd -P)
repo=$(CDPATH='' cd -- "$script_dir/.." && pwd -P)
branch=main
remote=origin
dry_run=false

usage() {
    printf 'Usage: %s [--repo PATH] [--branch NAME] [--remote NAME] [--dry-run]\n' "$0"
}

while [ "$#" -gt 0 ]; do
    case "$1" in
        --repo)
            [ "$#" -ge 2 ] || { usage >&2; exit 64; }
            repo=$2
            shift 2
            ;;
        --branch)
            [ "$#" -ge 2 ] || { usage >&2; exit 64; }
            branch=$2
            shift 2
            ;;
        --remote)
            [ "$#" -ge 2 ] || { usage >&2; exit 64; }
            remote=$2
            shift 2
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
            printf 'Unknown argument: %s\n' "$1" >&2
            usage >&2
            exit 64
            ;;
    esac
done

log_status() {
    status=$1
    shift
    message=$*
    message=$(printf '%s' "$message" | tr '\r\n' '  ')
    printf '%s host=%s status=%s message=%s\n' \
        "$(date --iso-8601=seconds)" "$(hostname)" "$status" "$message"
}

first_line() {
    printf '%s\n' "$1" | sed -n '1p'
}

command -v git >/dev/null 2>&1 || {
    log_status FAIL_CONFIG "git is not available"
    exit 4
}
command -v flock >/dev/null 2>&1 || {
    log_status FAIL_CONFIG "flock is not available"
    exit 4
}

if ! repo=$(CDPATH='' cd -- "$repo" 2>/dev/null && pwd -P); then
    log_status FAIL_CONFIG "Repository path does not exist: $repo"
    exit 4
fi

inside_worktree=$(git -C "$repo" rev-parse --is-inside-work-tree 2>/dev/null || true)
if [ "$inside_worktree" != true ]; then
    log_status FAIL_CONFIG "Not a Git worktree: $repo"
    exit 4
fi

top_level=$(git -C "$repo" rev-parse --show-toplevel 2>/dev/null || true)
if ! top_level=$(CDPATH='' cd -- "$top_level" 2>/dev/null && pwd -P) || [ "$top_level" != "$repo" ]; then
    log_status FAIL_CONFIG "RepoPath must be the Git worktree root"
    exit 4
fi

shallow=$(git -C "$repo" rev-parse --is-shallow-repository 2>/dev/null || true)
if [ "$shallow" != false ]; then
    log_status FAIL_CONFIG "Shallow repositories are not supported"
    exit 4
fi

sparse=$(git -C "$repo" config --bool core.sparseCheckout 2>/dev/null || true)
if [ "$sparse" = true ]; then
    log_status FAIL_CONFIG "Sparse checkouts are not supported"
    exit 4
fi

state_home=${XDG_STATE_HOME:-$HOME/.local/state}
lock_directory=$state_home/scripts-windows-git-sync
if ! mkdir -p "$lock_directory"; then
    log_status FAIL_CONFIG "Cannot create lock directory: $lock_directory"
    exit 4
fi
if ! { exec 9>"$lock_directory/sync.lock"; }; then
    log_status FAIL_CONFIG "Cannot open synchronization lock"
    exit 4
fi
if ! flock -n 9; then
    log_status SKIPPED_LOCKED "Another synchronization run holds the repository lock"
    exit 0
fi

fetch_refspec=+refs/heads/$branch:refs/remotes/$remote/$branch
if ! fetch_output=$(git -C "$repo" fetch --no-tags "$remote" "$fetch_refspec" 2>&1); then
    log_status FAIL_FETCH "$(first_line "$fetch_output")"
    exit 3
fi

if ! current_branch=$(git -C "$repo" symbolic-ref --quiet --short HEAD 2>/dev/null); then
    log_status SKIPPED_BRANCH "HEAD is detached"
    exit 0
fi
if [ "$current_branch" != "$branch" ]; then
    log_status SKIPPED_BRANCH "Current branch is $current_branch; expected $branch"
    exit 0
fi

expected_upstream=$remote/$branch
upstream=$(git -C "$repo" rev-parse --abbrev-ref --symbolic-full-name '@{u}' 2>/dev/null || true)
if [ "$upstream" != "$expected_upstream" ]; then
    log_status FAIL_CONFIG "Expected upstream $expected_upstream"
    exit 4
fi

for marker in MERGE_HEAD REBASE_HEAD CHERRY_PICK_HEAD REVERT_HEAD BISECT_LOG rebase-merge rebase-apply sequencer; do
    marker_path=$(git -C "$repo" rev-parse --git-path "$marker" 2>/dev/null || true)
    if [ -z "$marker_path" ]; then
        log_status FAIL_STATUS "Cannot inspect Git operation state"
        exit 4
    fi
    case "$marker_path" in
        /*) ;;
        *) marker_path=$repo/$marker_path ;;
    esac
    if [ -e "$marker_path" ]; then
        log_status FAIL_IN_PROGRESS "Git operation marker exists: $marker"
        exit 4
    fi
done

status_output=$(git -C "$repo" status --porcelain=v1 --untracked-files=all 2>&1)
status_code=$?
if [ "$status_code" -ne 0 ]; then
    log_status FAIL_STATUS "$(first_line "$status_output")"
    exit 4
fi
if [ -n "$status_output" ]; then
    log_status PAUSED_DIRTY "Worktree has tracked or untracked changes"
    exit 0
fi

remote_ref=refs/remotes/$remote/$branch
if ! git -C "$repo" rev-parse --verify "$remote_ref" >/dev/null 2>&1; then
    log_status FAIL_CONFIG "Remote-tracking ref does not exist: $remote_ref"
    exit 4
fi
remote_head_at_calculation=$(git -C "$repo" rev-parse "$remote_ref" 2>/dev/null || true)
local_head_at_calculation=$(git -C "$repo" rev-parse HEAD 2>/dev/null || true)
if [ -z "$remote_head_at_calculation" ] || [ -z "$local_head_at_calculation" ]; then
    log_status FAIL_STATUS "Cannot resolve local or remote HEAD"
    exit 4
fi

if ! counts=$(git -C "$repo" rev-list --left-right --count "HEAD...$remote_ref" 2>&1); then
    log_status FAIL_STATUS "$(first_line "$counts")"
    exit 4
fi
if [[ ! $counts =~ ^[0-9]+[[:space:]]+[0-9]+$ ]]; then
    log_status FAIL_STATUS "Unexpected ahead/behind output"
    exit 4
fi
read -r ahead behind <<EOF
$counts
EOF

if [ "$ahead" -gt 0 ] && [ "$behind" -gt 0 ]; then
    log_status FAIL_DIVERGED "Local is ahead by $ahead and behind by $behind commit(s)"
    exit 2
fi

pre_action_status=$(git -C "$repo" status --porcelain=v1 --untracked-files=all 2>&1)
pre_action_code=$?
if [ "$pre_action_code" -ne 0 ]; then
    log_status FAIL_STATUS "$(first_line "$pre_action_status")"
    exit 4
fi
if [ -n "$pre_action_status" ]; then
    log_status PAUSED_DIRTY "Worktree changed while the sync state was being calculated"
    exit 0
fi

pre_action_branch=$(git -C "$repo" symbolic-ref --quiet --short HEAD 2>/dev/null || true)
pre_action_head=$(git -C "$repo" rev-parse HEAD 2>/dev/null || true)
if [ "$pre_action_branch" != "$branch" ] || [ "$pre_action_head" != "$local_head_at_calculation" ]; then
    log_status STATE_CHANGED "Branch or HEAD changed while the sync state was being calculated"
    exit 0
fi

action_status=NOOP
if [ "$behind" -gt 0 ]; then
    if [ "$dry_run" = true ]; then
        log_status WOULD_PULL "Would fast-forward by $behind commit(s)"
        exit 0
    fi
    if ! merge_output=$(git -C "$repo" merge --ff-only "$remote_head_at_calculation" 2>&1); then
        log_status FAIL_PULL "$(first_line "$merge_output")"
        exit 4
    fi
    action_status=PULLED
elif [ "$ahead" -gt 0 ]; then
    if [ "$dry_run" = true ]; then
        if ! push_output=$(git -C "$repo" push --porcelain --dry-run "$remote" "$local_head_at_calculation:refs/heads/$branch" 2>&1); then
            log_status FAIL_PUSH_DRY_RUN "$(first_line "$push_output")"
            exit 3
        fi
        log_status WOULD_PUSH "Would push $ahead commit(s)"
        exit 0
    fi
    if ! push_output=$(git -C "$repo" push --porcelain "$remote" "$local_head_at_calculation:refs/heads/$branch" 2>&1); then
        log_status FAIL_PUSH "$(first_line "$push_output")"
        exit 3
    fi
    action_status=PUSHED
elif [ "$dry_run" = true ]; then
    log_status NOOP_DRY_RUN "Local and remote are aligned"
    exit 0
fi

if ! git -C "$repo" fetch --no-tags "$remote" "$fetch_refspec" >/dev/null 2>&1; then
    log_status POSTCONDITION_UNVERIFIED "Post-action fetch failed; no rollback was attempted"
    exit 4
fi

local_head=$(git -C "$repo" rev-parse HEAD 2>/dev/null || true)
remote_head=$(git -C "$repo" rev-parse "$remote_ref" 2>/dev/null || true)
post_status=$(git -C "$repo" status --porcelain=v1 --untracked-files=all 2>&1)
post_status_code=$?
if [ -z "$local_head" ] || [ -z "$remote_head" ] || [ "$post_status_code" -ne 0 ] || [ -n "$post_status" ]; then
    log_status FAIL_POSTCONDITION "Local main is not clean and aligned with $expected_upstream"
    exit 4
fi

if [ "$local_head" != "$remote_head" ]; then
    post_counts=$(git -C "$repo" rev-list --left-right --count "HEAD...$remote_ref" 2>/dev/null || true)
    read -r post_ahead post_behind <<EOF
$post_counts
EOF
    if [ "${post_ahead:-x}" = 0 ] && [ "${post_behind:-0}" -gt 0 ] 2>/dev/null; then
        log_status PENDING_REMOTE_MOVED "Remote advanced after this run; the next run will fast-forward"
        exit 0
    fi
    log_status FAIL_POSTCONDITION "Remote changed incompatibly during synchronization"
    exit 4
fi

log_status "$action_status" "Local main is clean and aligned with $expected_upstream at $local_head"
