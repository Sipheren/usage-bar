# Development

## Setup

```bash
scripts/setup-dev.sh        # once per clone: git hooks, config/local.env
scripts/install.sh          # tests, build, sign, install, open
```

## Build and test

```bash
swift build -c release
swift test
scripts/build.sh --no-install   # build and sign build/Usage Bar.app only
scripts/build.sh --replace      # also quit the running copy, install and open
```

The test suite uses synthetic data only. It never reads a real Keychain item, token cache, cookie
store or log.

## Versioning

- `VERSION` holds the version (SemVer). `build.sh` writes it to `CFBundleShortVersionString`.
- In a git checkout the build number is the commit count and About shows the short hash:
  "Version 0.1.0 (build 42 · abc1234)". Outside git the build number is a timestamp.
- Record every change in `CHANGELOG.md` (Keep a Changelog format) and bump `VERSION` when you release.

## Debug commands

Run them on the installed app, for example
`"/Applications/Usage Bar.app/Contents/MacOS/UsageBar" --help`.

| Command | What it does |
|---|---|
| `--demo` | Starts the app with sample data: no network, no credentials, nothing saved. |
| `--scan-costs` | Full cost scan of `~/.claude/projects` without the cache. Prints counts and totals only. |
| `--fetch-personal` | One real usage request for the Claude Code login. Prints the HTTP status and limit percentages. Spends rate-limit budget. |
| `--dump-desktop-shape` | Key names and value types of the Claude app's decrypted token cache, never values. Shows a Keychain prompt. |
| `--render-menubar DIR` | Draws the menu bar label in every style to PNG sample sheets. Makes one Claude Code usage request. |
| `--open-menu [secs]` | Opens the running app's menu, closing it after `secs` (default 6, at most 30). |
| `--close-menu` | Closes the menu. |
| `--open-settings general\|accounts\|about` | Opens Settings on a tab. |
| `--close-settings` | Closes Settings. |
| `--set-layout both\|one` | Switches the dropdown layout. |
| `--connect-work` | Same as clicking Connect… for the Claude app source (Keychain prompt). |
| `--unregister-login-item` | Removes the app from Login Items (used by `scripts/uninstall.sh`). |

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
scripts/qa/capture.sh screens/accounts.png --window
"$U" --close-settings

"$U" --open-menu 20
scripts/qa/capture.sh screens/dropdown.png --menu
"$U" --close-menu
```

- `scripts/qa/capture.sh` captures the main display, crops to the app's window or menu (or to
  `--rect X,Y,W,H` in points, for the menu bar item) and deletes the full-screen capture.
- The terminal app running it needs Screen Recording permission.
- A debug hook opens the window inactive (macOS doesn't let a background app take focus); the
  `open -a` line makes it active.
- `screens/` is not tracked: real captures show real account data. For images you commit, run the app
  with `--demo` (quit the normal copy first so only one menu bar item shows).
- Look at every capture before you keep it.

## Rules

- **Test first.** Write the failing test, watch it fail for the right reason, then write the code.
- **Never read real secrets in tooling.** No script, test or debug command prints a token, cookie or
  Keychain value. Debug commands print shapes, statuses and counts.
- **No personal data in tracked files.** `scripts/check-secrets.sh` runs as the pre-commit hook. Put
  your own name, employer, hostname and account id prefixes in `SECRET_SCAN_PRIVATE_PATTERNS` in
  `config/local.env` so the hook catches them without the list itself being committed.
- **Install with `scripts/build.sh --replace`** (or `scripts/install.sh`) after a change, then check
  the real UI.
- **Avoid rapid relaunches.** Each usage request spends the account's rate-limit budget. Launches skip
  accounts fetched within the refresh interval, but `--fetch-personal`, `--render-menubar`, Refresh Now
  and a fresh install's first launch all make requests.
- **Keep the bundle id** `com.sipheren.usagebar`. macOS ties Keychain, Full Disk Access and Login Items
  grants to it.
