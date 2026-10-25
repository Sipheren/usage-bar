#!/bin/bash
# One-time setup for a development checkout, on any platform (Git Bash on Windows): git hooks and
# local config.
set -euo pipefail

usage() {
    cat <<'EOF'
Usage: scripts/setup-dev.sh [--help]

Run once after cloning, from any folder (on Windows, in Git Bash):
  - points git at .githooks (the pre-commit hook runs scripts/check-secrets.sh --staged)
  - makes the shell scripts executable
  - creates config/local.env from the example if it doesn't exist yet; then put this machine's
    private words in it (SECRET_SCAN_PRIVATE_PATTERNS)

Safe to run again.
EOF
}

for arg in "$@"; do
    case "$arg" in
        -h|--help) usage; exit 0 ;;
        *) echo "Unknown option: $arg" >&2; usage >&2; exit 64 ;;
    esac
done

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

[[ -e .git ]] || { echo "error: not a git checkout ($ROOT)" >&2; exit 1; }

git config core.hooksPath .githooks
echo "  ok   git hooks: .githooks (pre-commit runs scripts/check-secrets.sh --staged)"

chmod +x .githooks/* scripts/*.sh
# Each platform folder keeps its own scripts; make whichever exist executable.
find . \( -name .git -o -name .build -o -name build -o -name bin -o -name obj -o -name target \) -prune \
    -o -path '*/scripts/*.sh' -type f -exec chmod +x {} +
echo "  ok   scripts are executable"

if [[ -f config/local.env ]]; then
    echo "  ok   config/local.env exists (not tracked)"
else
    cp config/local.env.example config/local.env
    chmod 600 config/local.env
    echo "  new  config/local.env created from the example: add this machine's private words to it"
fi

echo
case "$(uname -s)" in
    Darwin) echo "Next: macos/scripts/install.sh" ;;
    *) echo "Next: see README.md for this platform (the Windows port starts from docs/PORTING.md)" ;;
esac
