#!/bin/bash
# Scans the files that will be committed for secrets and personal data. Exits 1 on any hit.
set -uo pipefail

usage() {
    cat <<'EOF'
Usage: scripts/check-secrets.sh [--staged] [--list] [--help]

Scans files for secrets and personal data, and exits 1 if it finds any.

  (default)  every file that would be tracked: in a git checkout, tracked files plus untracked files
             that .gitignore does not exclude; outside git, every file not excluded by .gitignore
  --staged   only files staged for commit (what the pre-commit hook uses)
  --list     print the file list that would be scanned, then exit
  --help     show this help

Looks for: Anthropic keys and tokens, JWTs, sessionKey cookie values, access/refresh token values,
PEM private keys, AWS and GitHub tokens, email addresses (example.com/.org/.net are fine), home-folder
paths under /Users, UUIDs (unless listed in scripts/secret-scan-allow.txt), and any extra patterns
in SECRET_SCAN_PRIVATE_PATTERNS from config/local.env (names, employer, hostname...).

A line containing "secret-scan: allow" is skipped. Use that sparingly, for provably fake data.
Binary files are skipped.
EOF
}

MODE="all"
LIST_ONLY=0
for arg in "$@"; do
    case "$arg" in
        --staged) MODE="staged" ;;
        --list) LIST_ONLY=1 ;;
        -h|--help) usage; exit 0 ;;
        *) echo "Unknown option: $arg" >&2; usage >&2; exit 64 ;;
    esac
done

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT" || exit 2
GREP=/usr/bin/grep
[[ -x "$GREP" ]] || GREP="grep"
ALLOW_FILE="scripts/secret-scan-allow.txt"

PRIVATE_PATTERNS=""
if [[ -f config/local.env ]]; then
    PRIVATE_PATTERNS="$(set -a; . config/local.env >/dev/null 2>&1; printf '%s' "${SECRET_SCAN_PRIVATE_PATTERNS:-}")"
fi

# True when a path is excluded by .gitignore (the simple patterns this project uses).
is_ignored() {
    local f="$1" pat base
    [[ -f .gitignore ]] || return 1
    while IFS= read -r pat || [[ -n "$pat" ]]; do
        [[ -z "$pat" || "$pat" == \#* ]] && continue
        pat="${pat#/}"
        if [[ "$pat" == */ ]]; then
            pat="${pat%/}"
            [[ "$f" == "$pat"/* || "$f" == */"$pat"/* ]] && return 0
        else
            base="${f##*/}"
            # shellcheck disable=SC2053
            [[ "$f" == $pat || "$base" == $pat ]] && return 0
        fi
    done < .gitignore
    return 1
}

list_files() {
    if [[ -d .git ]] && command -v git >/dev/null 2>&1; then
        if [[ "$MODE" == "staged" ]]; then
            git diff --cached --name-only --diff-filter=ACMR
        else
            { git ls-files; git ls-files --others --exclude-standard; } | sort -u
        fi
    else
        find . \( -path ./.git -o -path ./.build -o -path ./build -o -path ./.swiftpm -o -path ./screens \) -prune \
            -o -type f -print | sed 's|^\./||' | sort | while IFS= read -r f; do
            is_ignored "$f" || printf '%s\n' "$f"
        done
    fi
}

FILES=()
while IFS= read -r f; do
    [[ -n "$f" && -f "$f" ]] && FILES+=("$f")
done < <(list_files)

if [[ $LIST_ONLY -eq 1 ]]; then
    printf '%s\n' "${FILES[@]}"
    exit 0
fi

is_allowed() {
    local kind="$1" m="$2" lower
    lower="$(printf '%s' "$m" | tr '[:upper:]' '[:lower:]')"
    case "$kind" in
        email)
            [[ "$lower" =~ @example\.(com|org|net)$ || "$lower" =~ \.example$ ]] && return 0
            # Retina asset names such as Icon@2x.png are not addresses.
            [[ "$lower" =~ @[0-9]x\.(png|jpe?g|gif|pdf|tiff?)$ ]] && return 0 ;;
        home-path)
            [[ "$m" == "/Users/Shared"* ]] && return 0 ;;
        uuid)
            [[ -f "$ALLOW_FILE" ]] && $GREP -qixF -- "$m" "$ALLOW_FILE" && return 0 ;;
    esac
    return 1
}

# kind|extended regex. The PEM marker is split so this file does not match itself.
CHECKS=(
    "anthropic-key|sk-ant-[A-Za-z0-9_-]{8,}"
    "jwt|eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{6,}"
    "session-cookie|sessionKey=[A-Za-z0-9%._-]{6,}"
    "token-value|(accessToken|refreshToken|access_token|refresh_token)\"?[[:space:]]*[:=][[:space:]]*\"[^\"[:space:]\\\\]{12,}\""
    "private-key|-----BEG""IN [A-Z ]*PRIVATE KEY"
    "aws-key|AKIA[0-9A-Z]{16}"
    "github-token|(gh[pousr]_[A-Za-z0-9]{36,}|github_pat_[A-Za-z0-9_]{22,})"
    "email|[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}"
    "home-path|/Users/[A-Za-z0-9._-]+"
    "uuid|[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}"
)
[[ -n "$PRIVATE_PATTERNS" ]] && CHECKS+=("private-word|$PRIVATE_PATTERNS")

HITS=0
for f in "${FILES[@]}"; do
    for check in "${CHECKS[@]}"; do
        kind="${check%%|*}"
        re="${check#*|}"
        while IFS= read -r hit; do
            lineno="${hit%%:*}"
            line="${hit#*:}"
            [[ "$line" == *"secret-scan: allow"* ]] && continue
            while IFS= read -r m; do
                [[ -z "$m" ]] && continue
                is_allowed "$kind" "$m" && continue
                # Show only the start of a match so a real secret is not echoed in full.
                printf '%s:%s: %s: %s…\n' "$f" "$lineno" "$kind" "${m:0:8}"
                HITS=$((HITS + 1))
            done < <(printf '%s\n' "$line" | $GREP -oE -- "$re")
        done < <($GREP -nIE -- "$re" "$f" 2>/dev/null)
    done
done

if [[ $HITS -gt 0 ]]; then
    echo "check-secrets: $HITS finding(s) in ${#FILES[@]} files. Remove them, or mark provably fake test data." >&2
    exit 1
fi
echo "check-secrets: ${#FILES[@]} files scanned, no secrets or personal data found."
