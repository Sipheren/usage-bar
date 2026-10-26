#!/bin/bash
# Real-screenshot QA: captures the main display and crops it to one of Usage Bar's windows.
set -euo pipefail

usage() {
    cat <<'USAGE'
Usage: macos/scripts/qa/capture.sh <output.png> [--window | --menu | --rect X,Y,W,H] [--help]

Takes a real screenshot of the main display and keeps only the part you ask for. The full-screen
capture is deleted straight away (it shows everything on screen).

  --window          the Settings window (default)
  --menu            the open dropdown menu (open it first: UsageBar --open-menu 20)
  --rect X,Y,W,H    a region in points from the top-left, e.g. the menu bar item
  --help            show this help

Needs Screen Recording permission for the app running this script (Terminal, iTerm...).
Look at every capture before you keep it; screens/ is not tracked because captures show real data.
USAGE
}

[[ $# -ge 1 && "$1" != "-h" && "$1" != "--help" ]] || { usage; exit 0; }
OUT="$1"; shift
MODE="window"; RECT=""
while [[ $# -gt 0 ]]; do
    case "$1" in
        --window) MODE="window" ;;
        --menu) MODE="menu" ;;
        --rect) MODE="rect"; RECT="${2:-}"; shift ;;
        -h|--help) usage; exit 0 ;;
        *) echo "Unknown option: $1" >&2; usage >&2; exit 64 ;;
    esac
    shift
done

MAC_ROOT="$(cd "$(dirname "$0")/../.." && pwd)"   # macos/
HELPER="$MAC_ROOT/build/qa/window-bounds"
if [[ ! -x "$HELPER" || "$MAC_ROOT/scripts/qa/window-bounds.swift" -nt "$HELPER" ]]; then
    mkdir -p "$MAC_ROOT/build/qa"
    xcrun swiftc -O "$MAC_ROOT/scripts/qa/window-bounds.swift" -o "$HELPER"
fi

if [[ "$MODE" == "rect" ]]; then
    IFS=, read -r X Y W H <<< "$RECT"
    SCALE="$(osascript -l JavaScript -e 'ObjC.import("AppKit"); $.NSScreen.mainScreen.backingScaleFactor' 2>/dev/null || echo 2)"
    SCALE="${SCALE%%.*}"
    [[ "$SCALE" =~ ^[0-9]+$ ]] || SCALE=2
else
    LAYER=0; [[ "$MODE" == "menu" ]] && LAYER=101
    read -r X Y W H SCALE < <("$HELPER" "Usage Bar" "$LAYER") \
        || { echo "error: no Usage Bar $MODE is on screen" >&2; exit 1; }
fi

TMP="$(mktemp -t usagebar-qa)"
FULL="$TMP.png"
trap 'rm -f "$TMP" "$FULL"' EXIT
screencapture -x -D 1 "$FULL"
cp "$FULL" "$OUT"
sips -c $((H * SCALE)) $((W * SCALE)) --cropOffset $((Y * SCALE)) $((X * SCALE)) "$OUT" >/dev/null
echo "Saved $OUT (${W}x${H} pt at ${X},${Y})"
