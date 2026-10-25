#!/bin/bash
# Removes Usage Bar and everything it stores. Never touches other apps' data.
set -euo pipefail

usage() {
    cat <<'EOF'
Usage: macos/scripts/uninstall.sh [--dry-run] [--yes] [--help]

Removes Usage Bar from this Mac:
  - its Login Items entry
  - the running app
  - /Applications/Usage Bar.app
  - ~/Library/Application Support/UsageBar (cost cache and saved account state)
  - its preferences (defaults domain com.sipheren.usagebar)

It does not touch Claude Code, the Claude app, Safari or their data.

  --dry-run   Show what would be removed, change nothing.
  --yes       Don't ask for confirmation.
  --help      Show this help.
EOF
}

DRY=0
YES=0
for arg in "$@"; do
    case "$arg" in
        --dry-run) DRY=1 ;;
        --yes) YES=1 ;;
        -h|--help) usage; exit 0 ;;
        *) echo "Unknown option: $arg" >&2; usage >&2; exit 64 ;;
    esac
done

APP="/Applications/Usage Bar.app"
EXEC="$APP/Contents/MacOS/UsageBar"
SUPPORT="$HOME/Library/Application Support/UsageBar"
DOMAIN="com.sipheren.usagebar"

run() {
    if [[ $DRY -eq 1 ]]; then
        echo "  would run: $*"
    else
        "$@"
    fi
}

state() { if [[ -e "$1" ]]; then echo "present"; else echo "not present"; fi; }

echo "Usage Bar uninstall$([[ $DRY -eq 1 ]] && echo " (dry run: nothing will change)")"
echo
echo "  App:          $APP ($(state "$APP"))"
echo "  App data:     ~/Library/Application Support/UsageBar ($(state "$SUPPORT"))"
if defaults read "$DOMAIN" >/dev/null 2>&1; then PREFS="present"; else PREFS="not present"; fi
echo "  Preferences:  defaults domain $DOMAIN ($PREFS)"
if pgrep -x UsageBar >/dev/null 2>&1; then RUNNING="running"; else RUNNING="not running"; fi
echo "  Process:      UsageBar ($RUNNING)"
echo

if [[ $DRY -eq 0 && $YES -eq 0 ]]; then
    read -r -p "Remove all of the above? [y/N] " answer
    [[ "$answer" =~ ^[Yy]$ ]] || { echo "Cancelled."; exit 0; }
fi

echo "==> Login item"
if [[ -x "$EXEC" ]]; then
    run "$EXEC" --unregister-login-item
else
    echo "  app not installed; nothing to unregister"
fi

echo "==> Quitting the app"
if [[ "$RUNNING" == "running" ]]; then
    run pkill -x UsageBar
    if [[ $DRY -eq 0 ]]; then
        for _ in $(seq 1 50); do pgrep -x UsageBar >/dev/null || break; sleep 0.2; done
    fi
else
    echo "  not running"
fi

echo "==> Removing files"
[[ -e "$APP" ]] && run rm -rf "$APP" || echo "  app: not present"
[[ -e "$SUPPORT" ]] && run rm -rf "$SUPPORT" || echo "  app data: not present"
if [[ "$PREFS" == "present" ]]; then run defaults delete "$DOMAIN"; else echo "  preferences: not present"; fi

cat <<'EOF'

Remove these by hand if you granted them (macOS doesn't let scripts change them):
  - System Settings > Privacy & Security > Full Disk Access: remove "Usage Bar".
  - Keychain Access > login > "Claude Safe Storage" > Access Control: remove "Usage Bar"
    from "Always allow access by these applications".
EOF
[[ $DRY -eq 1 ]] && echo && echo "Dry run complete. Nothing was changed."
exit 0
