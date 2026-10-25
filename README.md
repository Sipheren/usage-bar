# Usage Bar

Private project, all rights reserved.

Usage Bar is a macOS menu bar app that shows your Claude plan limits for two accounts at once:
the 5-hour session, the weekly limit and any model-scoped weekly limit. It also estimates what your
Claude Code usage would cost at API prices.

It reads sign-ins you already have (Claude Code, the Claude app, or Safari). It has no sign-in flow
of its own, never writes to another app's files and never stores a token.

![Menu bar](docs/images/menubar.png)

| Both accounts (default) | One at a time |
|---|---|
| ![Dropdown, both accounts](docs/images/dropdown-a.png) | ![Dropdown, one account](docs/images/dropdown-b.png) |

| General | Accounts | About |
|---|---|---|
| ![General settings](docs/images/settings-general.png) | ![Account settings](docs/images/settings-accounts.png) | ![About](docs/images/settings-about.png) |

The screenshots use demo mode, so every number and name in them is made up.

## Features

- Two (or three) accounts side by side, each with its own colour and tag.
- Menu bar styles: Bars (default), Percentages (`P 20% / 60%`), Compact (`P 20·60`) or Icon only. The
  label is always monochrome; usage never changes its colour.
- Dropdown with each limit's percentage, reset time, a pace marker and a pace sentence such as
  "7% under pace · lasts until reset" or "12% over pace · runs out around Wed 7 pm".
- Cost estimate from this Mac's Claude Code logs: today, last 30 days, tokens, a 30-day chart and the
  top model, at current API prices, shown in AUD.
- Extra usage (pay-as-you-go credits) when the plan has it.
- Rate-limit friendly: a 429 pauses one account and keeps its last numbers on screen.
- Launch at login (off by default).

## Requirements

- macOS 26 or later on Apple silicon.
- Xcode 26 or later (Swift 6.2 or later). The project builds with Xcode 27.1 and Swift 6.4.
- For the Claude Code source: Claude Code signed in on this Mac.
- For the Claude app source: the Claude desktop app signed in.
- For the Safari source: claude.ai signed in in Safari, and Full Disk Access for Usage Bar.

## Install

```bash
scripts/install.sh
```

The script checks the Mac, runs the tests, builds, signs, installs `/Applications/Usage Bar.app`
(quitting a running copy first) and opens it. Add `--skip-tests` to skip the tests.

Signing: macOS remembers permission grants (Keychain, Full Disk Access, Login Items) per signing
identity. Put a stable code-signing identity in `config/local.env` so the grants survive rebuilds:

```bash
cp config/local.env.example config/local.env
security find-identity -v -p codesigning     # pick a name from this list
# then set USAGEBAR_SIGN_IDENTITY="<that name>" in config/local.env
```

Without one, `build.sh` signs ad-hoc and warns you that every rebuild resets those grants.

## First-run setup

Open the menu bar item, then **Settings… > Accounts**. Each account has a **Sign-in source**:

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
  refresh interval (1, 2, 5 or 10 minutes) and Launch at login.
- **Settings… > Accounts** renames accounts, sets their tag and colour, and picks each sign-in source.
- The pace marker on each bar shows where usage would be if you spread it evenly over the window.

## Updating

```bash
git pull && scripts/install.sh
```

## Uninstalling

```bash
scripts/uninstall.sh --dry-run   # see what it would remove
scripts/uninstall.sh             # asks before removing anything
```

The script removes the login item, the app, its data folder and its preferences. Two grants need
removing by hand, and the script prints the steps: the Full Disk Access entry, and the Keychain
"Always Allow" on Claude Safe Storage.

## Where data lives

| What | Where |
|---|---|
| Settings, exchange rate, account emails | Preferences, domain `com.sipheren.usagebar` |
| Last usage numbers and fetch times per account | `~/Library/Application Support/UsageBar/state.json` |
| Claude Code cost cache (token counts only) | `~/Library/Application Support/UsageBar/cost-cache.json` |
| Logs | Unified log, subsystem `com.sipheren.usagebar` |

Read the logs with the full path (in zsh, plain `log` is a shell builtin). macOS keeps info lines
(successful fetches, cost scans) in memory only, so only `stream` shows them; notice and error lines
(429s, failures, demo mode starting) stay on disk for `show`:

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
| `GET https://open.er-api.com/v6/latest/USD` | USD to AUD rate, once a day | Nothing about you |

Details: [docs/DATA-SOURCES.md](docs/DATA-SOURCES.md).

## Troubleshooting

**"Paused by rate limit · retrying 6:40 am".** Anthropic answered 429. Usage Bar keeps the last numbers,
waits for the Retry-After time (5 minutes if none) and tries again. Repeated relaunches and Refresh Now
clicks cause most 429s; a launch skips any account fetched within the refresh interval.

**Safari source still says "Give Usage Bar Full Disk Access" after you granted it.** Quit Usage Bar
from its menu and open it again. macOS applies the grant on the next launch.

**"No claude.ai sign-in found in Safari".** Sign in at claude.ai in Safari, then click Refresh Now.

**Keychain prompt for "Claude Safe Storage".** That's the Claude app source asking once. Choose
**Always Allow**. If you clicked Deny, click **Connect…** in Accounts to ask again.

**The app icon has a grey frame, or Finder shows an old icon.** Rebuild with `scripts/install.sh`
(the icon comes from `design/icon/AppIcon.icon`). If Finder still shows the old one, macOS is using a
cached copy:

```bash
touch "/Applications/Usage Bar.app" && killall Dock
```

**Anything else.** Check the logs (above), then [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md).

## Project layout

```
Sources/UsageBarCore/   Logic with no UI: decoding, pace, formatting, pricing, cost scanner, credentials
Sources/UsageBar/       The app: menu bar item, dropdown, Settings window, debug commands, demo data
Tests/UsageBarTests/    XCTest suite (synthetic data only)
Resources/              Menu bar glyph (the Claude tray icon template)
design/icon/            App icon source (Icon Composer document and the script that draws it)
mockup/                 The original HTML design mockups
scripts/                build, install, uninstall, check-secrets, setup-dev, qa/capture
config/                 local.env.example (copy to local.env, which git ignores)
docs/                   Architecture, data sources, development, decisions, porting, spec, devlog
```

## Documentation

- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md): modules, data flow, polling, cost scanner, rendering.
- [docs/DATA-SOURCES.md](docs/DATA-SOURCES.md): each sign-in source, endpoints, response shapes, prices.
- [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md): build, test, debug commands, screenshot QA, rules.
- [docs/DECISIONS.md](docs/DECISIONS.md): why things are the way they are.
- [docs/PORTING.md](docs/PORTING.md): what carries over to Windows and Linux.
- [docs/SPEC.md](docs/SPEC.md): the product spec.
- [docs/DEVLOG.md](docs/DEVLOG.md) and [CHANGELOG.md](CHANGELOG.md): history.
- [AGENTS.md](AGENTS.md): working rules for coding agents.
