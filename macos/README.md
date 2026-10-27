# Usage Bar for macOS

The macOS app, and the reference implementation of [Usage Bar](../README.md). It is a native menu bar
app (Swift, AppKit and SwiftUI, no third-party dependencies).

Run every command below from the repository root.

## Requirements

- macOS 26 or later on Apple silicon.
- Xcode 26 or later (Swift 6.2 or later). The project builds with Xcode 27.1 and Swift 6.4.
- For the Claude Code source: Claude Code signed in on this Mac.
- For the Claude app source: the Claude desktop app signed in.
- For the Safari source: claude.ai signed in in Safari, and Full Disk Access for Usage Bar.

## Install

```bash
scripts/setup-dev.sh          # once per clone: git hooks, config/local.env
macos/scripts/install.sh
```

`install.sh` checks the Mac, runs the tests, builds, signs, installs `/Applications/Usage Bar.app`
(quitting a running copy first) and opens it. Add `--skip-tests` to skip the tests.

Signing: macOS remembers permission grants (Keychain, Full Disk Access, Login Items) per signing
identity. Put a stable code-signing identity in `config/local.env` so the grants survive rebuilds:

```bash
security find-identity -v -p codesigning     # pick a name from this list
# then set USAGEBAR_SIGN_IDENTITY="<that name>" in config/local.env
```

Without one, `build.sh` signs ad-hoc and warns you that every rebuild resets those grants. A free
Apple developer account or a self-signed code-signing certificate is enough.

## First-run setup

On first launch Usage Bar adds an account for each sign-in it can find without asking you anything:
the Claude Code login, and the Claude app when it's signed in to a different account. It never checks
Safari on first launch. If it finds nothing, it starts with no accounts and says so.

To add or remove accounts (one per sign-in source), open the menu bar item, then **Settings… > Accounts**.
Each account has a **Sign-in source**:

| Source | What it reads | Permission | What you do |
|---|---|---|---|
| Claude Code login | Claude Code's own Keychain item, through `/usr/bin/security` | None | Sign in to Claude Code once (`claude`). |
| Claude app login | The Claude desktop app's saved sign-in, decrypted with its "Claude Safe Storage" key | Keychain access to "Claude Safe Storage" | Click **Connect…**, then **Always Allow** in the macOS prompt. |
| Safari session | Safari's claude.ai cookies | Full Disk Access | Click **Open Privacy Settings…**, turn on Usage Bar, then quit and reopen Usage Bar. |

macOS applies a new Full Disk Access grant only to apps launched after you grant it, so the reopen
step matters. The status row turns green with the account's email or organization when a source
works, and names the exact problem when it doesn't.

## Using it

- Click the menu bar item for the dropdown. **Refresh Now** (⌘R) fetches immediately.
- **Settings… > General** sets the menu bar style, which limits it shows, the dropdown layout, the
  refresh interval (1, 2, 5 or 10 minutes; a Claude Code login checks at most every 5 minutes),
  Launch at login and the cost currency. Extra-usage amounts always stay in the currency your Claude
  plan bills in.
- **Settings… > Accounts** renames accounts, sets their tag and colour, and picks each sign-in source.
- The pace marker on each bar shows where usage would be if you spread it evenly over the window.

## Updating

```bash
git pull && macos/scripts/install.sh
```

## Uninstalling

```bash
macos/scripts/uninstall.sh --dry-run   # see what it would remove
macos/scripts/uninstall.sh             # asks before removing anything
```

The script removes the login item, the app, its data folder and its preferences. Two grants need
removing by hand, and the script prints the steps: the Full Disk Access entry, and the Keychain
"Always Allow" on Claude Safe Storage.

## Where data lives

| What | Where |
|---|---|
| Settings, exchange rates, account emails | Preferences, domain `com.sipheren.usagebar` (settings that couldn't be read are kept under `settings.v1.unreadable`) |
| Last usage numbers and fetch times per account | `~/Library/Application Support/UsageBar/state.json` |
| Claude Code cost cache (token counts only) | `~/Library/Application Support/UsageBar/cost-cache.json` |
| Logs | Unified log, subsystem `com.sipheren.usagebar` |

Read the logs with the full path (in zsh, plain `log` is a shell builtin). macOS keeps info lines
(successful fetches, cost scans) in memory only, so only `stream` shows them reliably; notice and
error lines (429s, failures, demo mode starting) stay on disk for `show`:

```bash
/usr/bin/log stream --predicate 'subsystem == "com.sipheren.usagebar"' --level info
/usr/bin/log show --last 1h --predicate 'subsystem == "com.sipheren.usagebar"' --info
```

## Privacy and security

- Read-only. Usage Bar never refreshes, writes or deletes another app's tokens, cookies, files or
  Keychain items.
- No stored secrets. Tokens and cookies live in memory for one request and nowhere else. Logs hold
  statuses, HTTP codes and counts, never tokens, cookies, emails or organization names.
- The cost cache holds token counts per day and model, never message content.
- Every network request it makes:

| Endpoint | Why | Sends |
|---|---|---|
| `GET https://api.anthropic.com/api/oauth/usage` | Plan limits | The account's OAuth access token |
| `GET https://api.anthropic.com/api/oauth/profile` | Account email and organization | Same token |
| `GET https://claude.ai/api/organizations` | Organizations for a cookie sign-in | The claude.ai session cookie |
| `GET https://claude.ai/api/organizations/{id}/usage` | Plan limits for a cookie sign-in | Same cookie |
| `GET https://open.er-api.com/v6/latest/USD` | Exchange rates, at most once a day, only when costs show in a currency other than USD | Nothing about you |

Details: [docs/DATA-SOURCES.md](../docs/DATA-SOURCES.md).

## Troubleshooting

**"Paused by rate limit · retrying 6:40 am".** Anthropic answered 429. Usage Bar keeps the last numbers,
waits for the Retry-After time (5 minutes if none or 0, an hour at most) and tries again. The Claude
Code login's usage endpoint allows only a few requests every few minutes, shared with Claude Code's own
`/usage`, so Usage Bar checks it at most every 5 minutes in the background. Repeated relaunches and
Refresh Now clicks cause most other 429s; a launch skips any account that isn't due.

**Safari source still says "Give Usage Bar Full Disk Access" after you granted it.** Quit Usage Bar
from its menu and open it again. macOS applies the grant on the next launch.

**"No claude.ai sign-in found in Safari".** Sign in at claude.ai in Safari, then click Refresh Now.

**Keychain prompt for "Claude Safe Storage".** That's the Claude app source asking once. Choose
**Always Allow**. If you clicked Deny, click **Connect…** in Accounts to ask again.

**Costs show in US$ although you picked another currency.** Usage Bar shows US dollars until it has
that currency's rate; it fetches the rate as soon as you pick one. Check the Exchange rate row in
General.

**The app icon has a grey frame, or Finder shows an old icon.** Rebuild with `macos/scripts/install.sh`
(the icon comes from `design/icon/AppIcon.icon`). If Finder still shows the old one, macOS is using a
cached copy:

```bash
touch "/Applications/Usage Bar.app" && killall Dock
```

**Anything else.** Check the logs (above), then [docs/macos/DEVELOPMENT.md](../docs/macos/DEVELOPMENT.md).

## This folder

```
Package.swift          SwiftPM package: UsageBarCore (logic), UsageBar (app), UsageBarTests
Sources/UsageBarCore/  Logic with no UI: decoding, pace, formatting, pricing, cost scanner, credentials
Sources/UsageBar/      The app: menu bar item, dropdown, Settings window, debug commands, demo data
Tests/UsageBarTests/   XCTest suite (synthetic data only)
VERSION                The macOS app's version (tags: macos-vX.Y.Z)
scripts/               build.sh, install.sh, uninstall.sh, qa/ (real-screenshot capture)
```

Build output goes to `macos/.build/` and `macos/build/`, which git ignores. Internals:
[docs/macos/ARCHITECTURE.md](../docs/macos/ARCHITECTURE.md). Workflow, debug commands and
screenshot QA: [docs/macos/DEVELOPMENT.md](../docs/macos/DEVELOPMENT.md).
