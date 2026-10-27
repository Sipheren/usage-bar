#!/bin/bash
# Builds a platform's source release zip and release notes from git, and can upload them as a draft
# GitHub release.
set -euo pipefail

usage() {
    cat <<'EOF'
Usage: scripts/release.sh macos|windows [--publish] [--help]

Builds dist/usage-bar-<platform>-<version>.zip and dist/notes-<platform>-<version>.md. The zip is
made with git archive from HEAD, never from the working folder, so untracked and ignored files can't
get in. The version comes from <platform>/VERSION at HEAD.

  macos | windows   The platform to release.
  --publish         Then create a draft GitHub release for the tag <platform>-v<version>, with the zip
                    and the notes (gh release create --draft). Publishing the draft is left to you.
  --help            Show this help.

It stops unless:
  - the tag <platform>-v<version> exists (--publish also needs it pushed)
  - the app's files at HEAD are identical to the tag (macOS: macos/ and design/icon/; Windows:
    windows/, design/icon/ and design/spark/). Only docs and scripts outside them may differ.
  - CHANGELOG.md at HEAD has a "## [macOS <version>]" or "## [Windows <version>]" section

The zip holds one folder, usage-bar-<platform>-<version>/. The files in scripts/release/<platform>/
go at its top; they are read from disk, so --publish refuses unless they are committed and unchanged
(without --publish that is only a warning).

The notes are that CHANGELOG section followed by the steps from INSTALL.txt.
EOF
}

PLATFORM=""
PUBLISH=0
for arg in "$@"; do
    case "$arg" in
        macos|windows)
            [[ -z "$PLATFORM" ]] || { echo "error: name one platform" >&2; exit 64; }
            PLATFORM="$arg" ;;
        --publish) PUBLISH=1 ;;
        -h|--help) usage; exit 0 ;;
        *) echo "Unknown option: $arg" >&2; usage >&2; exit 64 ;;
    esac
done
[[ -n "$PLATFORM" ]] || { echo "error: name a platform: macos or windows" >&2; usage >&2; exit 64; }

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO_ROOT"

fail() { echo "error: $*" >&2; exit 1; }
warn() { echo "warning: $*" >&2; }
ok() { echo "  ok   $*"; }

command -v git >/dev/null 2>&1 || fail "git is not installed."
[[ -e .git ]] || fail "not a git checkout: release zips are made from git."

# APP_PATHS: what the app is built from, which must match the tag. ZIP_PATHS: what goes in the zip.
case "$PLATFORM" in
    macos)
        LABEL="macOS"
        APP_PATHS=(macos design/icon)
        ZIP_PATHS=(macos design/icon config/local.env.example docs LICENSE README.md CHANGELOG.md
            ':(exclude)macos/scripts/qa')
        EXTRAS=(INSTALL.txt)
        LATEST="--latest" ;;
    windows)
        LABEL="Windows"
        # UsageBar.csproj builds in the tray spark (design/spark) and the icon layers (design/icon).
        APP_PATHS=(windows design/icon design/spark)
        ZIP_PATHS=(windows design/icon design/spark docs LICENSE README.md CHANGELOG.md)
        EXTRAS=(INSTALL.txt Install.cmd)
        LATEST="--latest=false" ;;
esac

echo "==> Checking the $LABEL release"
VERSION="$(git show "HEAD:$PLATFORM/VERSION" 2>/dev/null | tr -d '[:space:]')" || true
[[ -n "$VERSION" ]] || fail "$PLATFORM/VERSION is missing at HEAD."
[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+([-+][0-9A-Za-z.-]+)?$ ]] \
    || fail "$PLATFORM/VERSION at HEAD isn't a version number: $VERSION"
TAG="$PLATFORM-v$VERSION"
NAME="usage-bar-$PLATFORM-$VERSION"
ok "version $VERSION"

git rev-parse -q --verify "refs/tags/$TAG" >/dev/null || fail "tag $TAG doesn't exist. Tag the release first."
ok "tag $TAG"

if ! git diff --quiet "$TAG" HEAD -- "${APP_PATHS[@]}"; then
    git diff --stat "$TAG" HEAD -- "${APP_PATHS[@]}" >&2
    fail "HEAD changes the app's files since $TAG (above). Release the tagged code, or bump $PLATFORM/VERSION and tag again."
fi
ok "HEAD matches $TAG in ${APP_PATHS[*]}"

# The changelog section, header excluded, up to the next "## [" header, without blank edge lines.
CHANGELOG="$(git show HEAD:CHANGELOG.md 2>/dev/null)" || fail "CHANGELOG.md is missing at HEAD."
SECTION="$(awk -v hdr="## [$LABEL $VERSION]" '
    index($0, "## [") == 1 {
        if (found) done = 1
        else if (index($0, hdr) == 1) { found = 1; next }
    }
    found && !done {
        if (n == 0 && $0 ~ /^[[:space:]]*$/) next
        line[++n] = $0
        if ($0 !~ /^[[:space:]]*$/) last = n
    }
    END {
        for (i = 1; i <= last; i++) print line[i]
        if (!found) exit 2
    }' <<<"$CHANGELOG")" || fail "CHANGELOG.md at HEAD has no \"## [$LABEL $VERSION]\" section."
[[ -n "$SECTION" ]] || fail "the \"## [$LABEL $VERSION]\" section in CHANGELOG.md is empty."
ok "CHANGELOG.md section [$LABEL $VERSION]"

EXTRA_DIR="scripts/release/$PLATFORM"
EXTRA_ARGS=()
UNCOMMITTED=()
for f in "${EXTRAS[@]}"; do
    path="$EXTRA_DIR/$f"
    [[ -f "$path" ]] || fail "$path is missing."
    if ! git cat-file -e "HEAD:$path" 2>/dev/null || [[ -n "$(git status --porcelain -- "$path")" ]]; then
        UNCOMMITTED+=("$path")
    fi
    EXTRA_ARGS+=("--add-file=$path")
done
if [[ ${#UNCOMMITTED[@]} -gt 0 ]]; then
    [[ $PUBLISH -eq 0 ]] || fail "commit these first (the zip takes them from disk): ${UNCOMMITTED[*]}"
    warn "not committed, or changed since HEAD: ${UNCOMMITTED[*]}. Fine for a check; --publish refuses."
else
    ok "$EXTRA_DIR is committed"
fi

DIST="dist"
ZIP="$DIST/$NAME.zip"
NOTES="$DIST/notes-$PLATFORM-$VERSION.md"
mkdir -p "$DIST"
rm -f "$ZIP" "$NOTES"

echo "==> Building $ZIP from HEAD"
# --prefix must come before --add-file: each added file lands at <prefix><its basename>.
git archive --format=zip --prefix="$NAME/" "${EXTRA_ARGS[@]}" -o "$ZIP" HEAD -- "${ZIP_PATHS[@]}"
ok "$ZIP ($(( $(wc -c < "$ZIP") / 1024 )) KB)"

# INSTALL.txt's first line is its title; the notes use their own heading instead.
{
    printf '%s\n\n## Install\n\n' "$SECTION"
    # shellcheck disable=SC2016  # literal Markdown backticks
    printf 'Download `%s` from the assets below.\n\n' "$NAME.zip"
    awk 'NR == 1 { next } !started && /^[[:space:]]*$/ { next } { started = 1; print }' "$EXTRA_DIR/INSTALL.txt"
} > "$NOTES"
ok "$NOTES"

if [[ $PUBLISH -eq 0 ]]; then
    echo
    echo "Nothing was uploaded. Check the zip and the notes, then: scripts/release.sh $PLATFORM --publish"
    exit 0
fi

echo "==> Creating a draft GitHub release for $TAG"
command -v gh >/dev/null 2>&1 || fail "the GitHub CLI (gh) is not installed."
# gh release view finds drafts too. Anything other than "not found" is a failed check, not a go-ahead.
if VIEW="$(gh release view "$TAG" 2>&1)"; then
    fail "a release for $TAG already exists (draft or published). See it with: gh release view $TAG --web"
elif [[ "$VIEW" != *"release not found"* ]]; then
    echo "$VIEW" >&2
    fail "couldn't check for an existing $TAG release (gh output above)."
fi
ok "no release for $TAG yet"

URL="$(gh release create "$TAG" "$ZIP" --verify-tag --draft --title "Usage Bar for $LABEL $VERSION" \
    --notes-file "$NOTES" "$LATEST")" || fail "gh release create failed (output above)."

cat <<EOF

Draft release: $URL
Check it on GitHub, then publish it with:
  gh release edit $TAG --draft=false $LATEST
EOF
