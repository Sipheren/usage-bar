# macOS development

Commands run from the repository root. Rules for every platform are in [AGENTS.md](../../AGENTS.md).

## Setup

```bash
scripts/setup-dev.sh             # once per clone: git hooks, config/local.env
macos/scripts/install.sh         # tests, build, sign, install, open
```

## Build and test

```bash
(cd macos && swift build -c release)
(cd macos && swift test)
macos/scripts/build.sh --no-install   # build and sign macos/build/Usage Bar.app only
macos/scripts/build.sh --replace      # also quit the running copy, install and open
```

The test suite uses synthetic data only. It never reads a real Keychain item, token cache, cookie
store or log.

## The menu bar spark

`macos/Sources/UsageBar/ClaudeSparkPath.swift` is generated. To regenerate it (needs numpy and
Pillow, and a Claude app install to trace from):

```bash
python3 design/spark/trace_spark.py
```

It writes the Swift path and `design/spark/ClaudeSpark.svg`. Compare the result with the original
at 16 pt on the real menu bar before you commit it.

## Versioning

- `macos/VERSION` holds the macOS app's version (SemVer). `build.sh` writes it to
  `CFBundleShortVersionString`. Releases are tagged `macos-vX.Y.Z`.
- In a git checkout the build number is the repository's commit count and About shows the short hash:
  "Version 0.2.0 (build 42 · abc1234)". Outside git the build number is a timestamp.
- Record every change in `CHANGELOG.md` (Keep a Changelog format, each entry naming its platform) and
  bump `macos/VERSION` when you release.

## Debug commands

Run them on the installed app, for example
`"/Applications/Usage Bar.app/Contents/MacOS/UsageBar" --help`.

| Command | What it does |
|---|---|
| `--demo` | Starts the app with sample data: no network, no credentials, nothing saved. |
| `--scan-costs` | Full cost scan of `~/.claude/projects` without the cache. Prints counts and totals only. |
| `--fetch-personal` | One real usage request for the Claude Code login. Prints the HTTP status and limit percentages. Spends rate-limit budget. |
| `--dump-desktop-shape` | Key names and value types of the Claude app's decrypted token cache, never values. Shows a Keychain prompt. |
| `--export-fixtures DIR` | Fetches each due account once (Personal: OAuth usage and profile; Work: claude.ai organizations and org usage), writes anonymised copies to `DIR/api` and then `DIR/expected.json`. Raw responses stay in memory; a leak check gates every file. Files already in `DIR/api` are kept. Quit the app first and launch it with `open -n -W --stdout LOG "/Applications/Usage Bar.app" --args --export-fixtures DIR` so its own Full Disk Access applies. The output is local test data: never commit it. |
| `--fixtures-expected DIR` | Regenerates `DIR/expected.json` (the answer sheet built by `ParitySheet`) from the files in DIR. No network. `USAGEBAR_FIXTURES=DIR swift test` then checks it for drift. |
| `--render-menubar DIR` | Draws the menu bar label in every style to PNG sample sheets. Makes one Claude Code usage request. |
| `--open-menu [secs]` | Opens the running app's menu, closing it after `secs` (default 6, at most 30). |
| `--close-menu` | Closes the menu. |
| `--open-settings general\|accounts\|about` | Opens Settings on a tab. |
| `--close-settings` | Closes Settings. |
| `--set-layout both\|one` | Switches the dropdown layout. |
| `--connect-work` | Same as clicking Connect… for the Claude app source (Keychain prompt). |
| `--unregister-login-item` | Removes the app from Login Items (used by `macos/scripts/uninstall.sh`). |

The `--open-*`, `--close-settings`, `--set-layout` and `--connect-work` commands send a distributed
notification to the running app and return at once. While the menu is open, macOS holds distributed
notifications back until it closes, so `--close-menu` uses a Darwin notification instead, which the app
checks every 0.2 s while the menu is open. Close the menu before sending any other command.

## Logs

```bash
/usr/bin/log stream --predicate 'subsystem == "com.sipheren.usagebar"' --level info
/usr/bin/log show --last 1h --predicate 'subsystem == "com.sipheren.usagebar" AND category == "fetch"' --info
```

Categories: `fetch` (each fetch result and every 429), `cost` (scan counts), `ui`, `auth` (which
credential was read and whether it worked). Log statuses, source kinds, HTTP codes and counts. Never
log a token, a cookie, an email, an organization name or a path under the home folder, and mark only
safe values `.public`.

## Real-screenshot QA

Judge the UI from real screenshots. Previews and `ImageRenderer` renders leave out grouped forms and
native controls.

```bash
U="/Applications/Usage Bar.app/Contents/MacOS/UsageBar"
"$U" --open-settings accounts
open -a "/Applications/Usage Bar.app"         # brings it forward so it renders as the active window
macos/scripts/qa/capture.sh screens/accounts.png --window
"$U" --close-settings

"$U" --open-menu 20
macos/scripts/qa/capture.sh screens/dropdown.png --menu
"$U" --close-menu
```

- `macos/scripts/qa/capture.sh` captures the main display, crops to the app's window or menu (or to
  `--rect X,Y,W,H` in points, for the menu bar item) and deletes the full-screen capture.
- The terminal app running it needs Screen Recording permission.
- A debug hook opens the window inactive (macOS doesn't let a background app take focus); the
  `open -a` line makes it active.
- `screens/` is not tracked: real captures show real account data. For images you commit (they go in
  `docs/images/`), run the app with `--demo` (quit the normal copy first so only one menu bar item
  shows).
- Look at every capture before you keep it.

## Rules

- **Test first.** Write the failing test, watch it fail for the right reason, then write the code.
- **Never read real secrets in tooling.** No script, test or debug command prints a token, cookie or
  Keychain value. Debug commands print shapes, statuses and counts.
- **No personal data in tracked files.** `scripts/check-secrets.sh` runs as the pre-commit hook. Put
  your own name, employer, hostname and account id prefixes in `SECRET_SCAN_PRIVATE_PATTERNS` in
  `config/local.env` so the hook catches them without the list itself being committed.
- **Install with `macos/scripts/build.sh --replace`** (or `macos/scripts/install.sh`) after a change,
  then check the real UI.
- **Avoid rapid relaunches.** Each usage request spends the account's rate-limit budget. Launches skip
  accounts fetched within the refresh interval, but `--fetch-personal`, `--render-menubar`, Refresh Now
  and a fresh install's first launch all make requests.
- **Keep the bundle id** `com.sipheren.usagebar`. macOS ties Keychain, Full Disk Access and Login Items
  grants to it.
