#!/bin/bash
# One-command install: checks requirements, runs the tests, builds, signs and installs Usage Bar.
set -euo pipefail

usage() {
    cat <<'EOF'
Usage: scripts/install.sh [--skip-tests] [--help]

Checks the Mac, runs the test suite, then builds, signs and installs "/Applications/Usage Bar.app",
replacing a running copy. Safe to run again: it is also how you update.

  --skip-tests   Don't run `swift test` first.
  --help         Show this help.

Requirements: macOS 26 or later, Apple silicon, Xcode (or the command-line tools) with Swift 6.
Signing: set USAGEBAR_SIGN_IDENTITY or create config/local.env (see config/local.env.example).
EOF
}

SKIP_TESTS=0
for arg in "$@"; do
    case "$arg" in
        --skip-tests) SKIP_TESTS=1 ;;
        -h|--help) usage; exit 0 ;;
        *) echo "Unknown option: $arg" >&2; usage >&2; exit 64 ;;
    esac
done

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

fail() { echo "error: $*" >&2; exit 1; }
ok() { echo "  ok   $*"; }

echo "==> Checking this Mac"
MACOS="$(sw_vers -productVersion)"
[[ "${MACOS%%.*}" -ge 26 ]] || fail "macOS 26 or later is required (this Mac has $MACOS)."
ok "macOS $MACOS"
[[ "$(uname -m)" == "arm64" ]] || fail "Apple silicon (arm64) is required."
ok "Apple silicon"
command -v xcrun >/dev/null 2>&1 || fail "Xcode command-line tools are missing. Run: xcode-select --install"
SWIFT_VERSION="$(xcrun swift --version 2>/dev/null | head -1)" || fail "Swift toolchain not found. Install Xcode."
[[ "$SWIFT_VERSION" =~ Swift\ version\ ([0-9]+) ]] && [[ "${BASH_REMATCH[1]}" -ge 6 ]] \
    || fail "Swift 6 or later is required (found: $SWIFT_VERSION)."
ok "$SWIFT_VERSION"

if [[ $SKIP_TESTS -eq 0 ]]; then
    echo "==> Running tests"
    TEST_LOG="$(mktemp -t usagebar-tests)"
    if swift test >"$TEST_LOG" 2>&1; then
        echo "  ok   $(grep -E 'Executed [0-9]+ tests' "$TEST_LOG" | tail -1 | sed -E 's/^[[:space:]]+//')"
        rm -f "$TEST_LOG"
    else
        tail -40 "$TEST_LOG"
        rm -f "$TEST_LOG"
        fail "Tests failed (output above). Fix them, or rerun with --skip-tests."
    fi
fi

echo "==> Building and installing"
"$ROOT/scripts/build.sh" --replace

cat <<'EOF'

Usage Bar is installed and running (look for the spark in the menu bar).

First run:
  1. Open the menu bar item, then Settings… > Accounts.
  2. For each account pick a sign-in source:
     - Claude Code login: nothing to do if `claude` is signed in on this Mac.
     - Claude app login: click Connect…, then "Always Allow" when macOS asks about Claude Safe Storage.
     - Safari session: click Open Privacy Settings…, turn on Usage Bar under Full Disk Access,
       then quit and reopen Usage Bar (macOS applies the grant on the next launch).
  3. Optional: Settings… > General > Launch at login.

Details: README.md, "First-run setup".
EOF
