#!/bin/bash
# Builds Usage Bar into build/Usage Bar.app, signs it, and (by default) installs it to /Applications.
set -euo pipefail

usage() {
    cat <<'EOF'
Usage: scripts/build.sh [--no-install | --replace] [--help]

Builds "Usage Bar.app" in build/, signs it, installs it to /Applications and opens it.

  --no-install   Build and sign build/Usage Bar.app only. Never touches /Applications or a running copy.
  --replace      Quit the running copy first (waits up to 10 s), then install and open.
  --help         Show this help.

Without either flag the script refuses to install over a running copy.

Signing identity, first match wins:
  1. USAGEBAR_SIGN_IDENTITY in the environment
  2. USAGEBAR_SIGN_IDENTITY in config/local.env (not tracked; copy config/local.env.example)
  3. ad-hoc signing, with a warning: Keychain and Full Disk Access grants won't survive rebuilds

Version: VERSION holds the marketing version. In a git checkout the build number is the commit
count and the short commit hash is shown in About; elsewhere the build number is a timestamp.
EOF
}

NO_INSTALL=0
REPLACE=0
for arg in "$@"; do
    case "$arg" in
        --no-install) NO_INSTALL=1 ;;
        --replace) REPLACE=1 ;;
        -h|--help) usage; exit 0 ;;
        *) echo "Unknown option: $arg" >&2; usage >&2; exit 64 ;;
    esac
done

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
APP_NAME="Usage Bar"
BUNDLE="$ROOT/build/$APP_NAME.app"
INSTALLED="/Applications/$APP_NAME.app"
EXEC="UsageBar"
BUNDLE_ID="com.sipheren.usagebar"
ICON_DOC="$ROOT/design/icon/AppIcon.icon"

[[ -f "$ROOT/VERSION" ]] || { echo "error: VERSION file missing" >&2; exit 1; }
VERSION="$(tr -d '[:space:]' < "$ROOT/VERSION")"
BUILD_DATE="$(date '+%d %b %Y %H:%M')"
GIT_COMMIT=""
if [[ -d "$ROOT/.git" ]] && command -v git >/dev/null 2>&1; then
    BUILD_NUMBER="$(git -C "$ROOT" rev-list --count HEAD 2>/dev/null || true)"
    GIT_COMMIT="$(git -C "$ROOT" rev-parse --short HEAD 2>/dev/null || true)"
fi
[[ -n "${BUILD_NUMBER:-}" ]] || BUILD_NUMBER="$(date +%Y%m%d%H%M)"

# Signing identity: environment first, then config/local.env, else ad-hoc.
if [[ -z "${USAGEBAR_SIGN_IDENTITY:-}" && -f "$ROOT/config/local.env" ]]; then
    # shellcheck disable=SC1091
    set -a; . "$ROOT/config/local.env"; set +a
fi
IDENTITY="${USAGEBAR_SIGN_IDENTITY:-}"
if [[ -z "$IDENTITY" ]]; then
    IDENTITY="-"
    SIGNED_BY="ad-hoc"
    cat >&2 <<'EOF'
warning: no signing identity configured; signing ad-hoc.
         Ad-hoc signatures change on every build, so macOS forgets the Keychain "Always Allow"
         and Full Disk Access grants each time. Set USAGEBAR_SIGN_IDENTITY (see config/local.env.example).
EOF
else
    SIGNED_BY="$IDENTITY"
    if ! security find-identity -v -p codesigning 2>/dev/null | grep -qF "\"$IDENTITY\""; then
        echo "error: signing identity \"$IDENTITY\" not found in the keychain (security find-identity -v -p codesigning)" >&2
        exit 1
    fi
fi

is_running() {
    pgrep -f "$INSTALLED/Contents/MacOS/$EXEC" >/dev/null 2>&1
}

if [[ $NO_INSTALL -eq 0 && $REPLACE -eq 0 ]] && is_running; then
    echo "Usage Bar is running — quit it first (or use --replace)"
    exit 1
fi

echo "==> swift build -c release (version $VERSION, build $BUILD_NUMBER${GIT_COMMIT:+, $GIT_COMMIT})"
cd "$ROOT"
swift build -c release
BIN="$(swift build -c release --show-bin-path)/$EXEC"
[[ -x "$BIN" ]] || { echo "error: built binary not found at $BIN" >&2; exit 1; }

echo "==> Assembling $BUNDLE"
rm -rf "$BUNDLE"
mkdir -p "$BUNDLE/Contents/MacOS" "$BUNDLE/Contents/Resources"
cp "$BIN" "$BUNDLE/Contents/MacOS/$EXEC"
# Claude menu bar glyph (the tray icon template from the Claude app).
for f in ClaudeSpark.png ClaudeSpark@2x.png ClaudeSpark@3x.png; do
    [[ -f "$ROOT/Resources/$f" ]] || { echo "error: missing Resources/$f" >&2; exit 1; }
    cp "$ROOT/Resources/$f" "$BUNDLE/Contents/Resources/$f"
done

# App icon: compile the Icon Composer document with actool into Assets.car (+ an .icns fallback).
# macOS 26 applies the icon shape itself; a pre-shaped PNG/.icns gets a grey frame instead.
ICON_PLIST=""
if [[ -d "$ICON_DOC" ]]; then
    echo "==> Compiling the app icon with actool"
    ICON_TMP="$ROOT/build/icon-compile"
    rm -rf "$ICON_TMP"; mkdir -p "$ICON_TMP"
    xcrun actool "$ICON_DOC" --compile "$ICON_TMP" --app-icon AppIcon --platform macosx \
        --minimum-deployment-target 26.0 --target-device mac \
        --output-partial-info-plist "$ICON_TMP/partial.plist" --output-format human-readable-text \
        --errors --warnings >/dev/null
    [[ -f "$ICON_TMP/Assets.car" ]] || { echo "error: actool produced no Assets.car" >&2; exit 1; }
    cp "$ICON_TMP/Assets.car" "$BUNDLE/Contents/Resources/Assets.car"
    [[ -f "$ICON_TMP/AppIcon.icns" ]] && cp "$ICON_TMP/AppIcon.icns" "$BUNDLE/Contents/Resources/AppIcon.icns"
    rm -rf "$ICON_TMP"
    ICON_PLIST="    <key>CFBundleIconFile</key>
    <string>AppIcon</string>
    <key>CFBundleIconName</key>
    <string>AppIcon</string>"
else
    echo "==> No $ICON_DOC; building without an app icon"
fi

cat > "$BUNDLE/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleIdentifier</key>
    <string>$BUNDLE_ID</string>
    <key>CFBundleName</key>
    <string>$APP_NAME</string>
    <key>CFBundleDisplayName</key>
    <string>$APP_NAME</string>
    <key>CFBundleExecutable</key>
    <string>$EXEC</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleShortVersionString</key>
    <string>$VERSION</string>
    <key>CFBundleVersion</key>
    <string>$BUILD_NUMBER</string>
    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>
    <key>LSMinimumSystemVersion</key>
    <string>26.0</string>
    <key>LSUIElement</key>
    <true/>
    <key>NSHighResolutionCapable</key>
    <true/>
$ICON_PLIST
    <key>UBBuildDate</key>
    <string>$BUILD_DATE</string>
    <key>UBSigningIdentity</key>
    <string>$SIGNED_BY</string>
    <key>UBGitCommit</key>
    <string>$GIT_COMMIT</string>
    <key>UBProjectPath</key>
    <string>$ROOT</string>
</dict>
</plist>
PLIST
plutil -lint "$BUNDLE/Contents/Info.plist" >/dev/null

echo "==> Signing ($SIGNED_BY)"
codesign --force --options runtime --sign "$IDENTITY" "$BUNDLE"
codesign --verify --strict "$BUNDLE"

if [[ $NO_INSTALL -eq 1 ]]; then
    echo "Built and signed $BUNDLE (not installed)."
    exit 0
fi

if is_running; then
    if [[ $REPLACE -eq 1 ]]; then
        echo "==> Quitting the running copy"
        pkill -x "$EXEC" || true
        for _ in $(seq 1 50); do is_running || break; sleep 0.2; done
        if is_running; then echo "error: Usage Bar did not quit within 10 s" >&2; exit 1; fi
    else
        echo "Usage Bar is running — quit it first (or use --replace)"
        exit 1
    fi
fi

echo "==> Installing to $INSTALLED"
rm -rf "$INSTALLED"
ditto "$BUNDLE" "$INSTALLED"

echo "==> Opening"
open "$INSTALLED"
echo "Done: Usage Bar $VERSION (build $BUILD_NUMBER) installed."
