#!/bin/bash
# One-time setup for a development checkout: git hooks and local config.
set -euo pipefail

usage() {
    cat <<'EOF'
Usage: scripts/setup-dev.sh [--help]

Run once after cloning:
  - points git at .githooks (the pre-commit hook runs scripts/check-secrets.sh --staged)
  - makes the scripts executable
  - creates config/local.env from the example if it doesn't exist yet

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

[[ -d .git ]] || { echo "error: not a git checkout ($ROOT)" >&2; exit 1; }

git config core.hooksPath .githooks
echo "  ok   git hooks: .githooks (pre-commit runs scripts/check-secrets.sh --staged)"

chmod +x .githooks/* scripts/*.sh scripts/qa/*.sh
echo "  ok   scripts are executable"

if [[ -f config/local.env ]]; then
    echo "  ok   config/local.env exists (not tracked)"
else
    cp config/local.env.example config/local.env
    chmod 600 config/local.env
    echo "  new  config/local.env created from the example: set USAGEBAR_SIGN_IDENTITY in it"
fi

echo
echo "Next: scripts/install.sh"
