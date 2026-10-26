# Usage Bar: product spec

A menu bar app that shows Claude plan limits for one or more accounts at once (up to one per sign-in
source), with a cost estimate from Claude Code logs. It is Claude-only and multi-account. This spec describes the macOS app (macOS 26+,
Apple silicon), the reference implementation; the Windows app in `windows/` keeps the same behaviour with
Windows equivalents of the macOS parts, and tray icons in each account's colour (see
[PORTING.md](PORTING.md) and decision 17).

Visual reference: the design mockups in `design/mockup/` (`Main.dc.html` for menu bar styles,
`DropdownA.dc.html` and `DropdownB.dc.html` for the two dropdown layouts). Look: system font, a light
popover-style card, a pace marker on every bar, and a colour per account: clay `#D97757` for Personal
(Claude Code), blue `#3E7CC4` for Work (Safari) and violet `#8E6AC8` for the Claude app account. Badges
use `#B4532F` and `#2563A8` for the first two, otherwise the account colour at 80% brightness.

## Accounts

Each account has an id, a name, a tag of one or two characters, a colour, a "show in menu bar" switch,
a sign-in source and an optional logs folder for the cost block.

Sign-in sources (details in [DATA-SOURCES.md](DATA-SOURCES.md)):

1. **Claude Code login.** Reads Claude Code's Keychain item through `/usr/bin/security` on every poll.
   Logs: `~/.claude/projects`.
2. **Claude app login.** Reads and decrypts the Claude desktop app's saved sign-in, after a one-time
   Keychain approval for "Claude Safe Storage". Falls back to the app's claude.ai session cookie.
3. **Safari session.** Reads the claude.ai session cookie from Safari's cookie files (needs Full Disk
   Access).

All three are read-only. There is no sign-in flow.

**One or more accounts, up to one per sign-in source.** Settings > Accounts > Add Account offers each
source once, and greys out a source already in use; any account can switch source. Zero accounts is a
valid state. The default account for each source:

| Source | Name | Tag | Colour |
|---|---|---|---|
| Claude Code login | Personal | `P` | clay |
| Claude app login | Claude app | `C` | violet |
| Safari session | Work | `W` | blue |

**First run adds only what's signed in**, with no prompt (details in
[DATA-SOURCES.md](DATA-SOURCES.md#first-run-detection)):

- Claude Code: its Keychain item exists (looked up by attributes; the secret isn't read).
- Claude app: its `config.json` has a non-empty `oauth:tokenCacheV2` and a `lastKnownAccountUuid`,
  and that uuid isn't the Claude Code account's (then they're the same account and the fetcher would
  refuse it). The account then shows its normal "Connect…" state until the one-time Keychain approval.
- Safari is never probed on first run (it needs Full Disk Access). Users add it from Settings.
- Nothing found: no accounts. First run saves the result straight away so account ids stay stable.

Settings saved by an earlier version load unchanged: no migration adds, removes or renames accounts.

## Polling

- Every refresh interval: 1, 2, 5 or 10 minutes, default 2.
- A launch or opening the menu fetches an account only when its last attempt is at least one interval
  old (persisted across launches). Refresh Now always fetches unless the account is inside a
  Retry-After window.
- On 429: honour `Retry-After`, else wait 5 minutes; keep the last numbers and show "Paused by rate
  limit · retrying <time>" in the account header. Show an error only when there is no data yet.
- Fetch accounts concurrently; timers run in `.common` run-loop mode.

## Menu bar

The label is drawn as a template image and never changes colour with usage. It starts with the
Claude spark (a vector trace, 16 pt), then for each account shown, in order, an outlined tag and its values.
With no account shown (none set up, or every account's "Show in menu bar" off) it is the spark alone in
every style.

| Style | Example |
|---|---|
| Bars (default) | two (or three) 26×4 pt bars per account, stacked |
| Percentages | `P 20% / 60%  W 45% / 72%` |
| Compact | `P 20·60  W 45·72` |
| Icon only | the glyph alone |

Metrics (Settings): 5-hour session (on), weekly (on), model weekly such as Fable (off), always in that
order. An account with no data shows its tag and "–". Data older than 15 minutes draws at 50% opacity.

## Dropdown

An `NSMenu` with one SwiftUI view (360 pt wide) that updates live, then native items with SF Symbols:
Refresh Now (⌘R), About Usage Bar, Settings… (⌘,), Quit Usage Bar (⌘Q).

- **Layout A, all accounts (default).** Header "Claude" and "Updated N min ago". Per account, one under
  the other with a rule between: badge, name and "Today A$x"; compact limit rows (name, "% · resets …",
  bar with pace marker, pace line); extra usage; "30 days A$x · N tokens".
- **Layout B, one at a time.** A tab per account (names truncate with "…"; no tab strip when there is
  only one account), then large limit rows; a 2×2 grid (Today, 30 days,
  Today tokens, 30-day tokens); a 30-day cost chart with its maximum; the top model; the footnote "Cost
  and tokens estimated from this Mac's Claude Code logs at API rates."
- **No accounts, either layout.** Header "Claude", then "No accounts", a line saying no Claude sign-in
  was found, and an **Open Settings…** button that closes the menu and opens Settings on the Accounts
  tab.

Per limit:

- Name: "Session", "Weekly" or "<model> weekly".
- Reset: "in 2h 10m" under 24 hours, else "Thu 9:00 am" (local time, rounded to the minute). A window
  that hasn't started reads "0% · starts on first use" and has no pace line.
- Pace: `expected` = elapsed share of the window × 100; `delta` = used − expected.
  - |delta| ≤ 2 (rounded): "On pace · lasts until reset".
  - delta < −2: "N% under pace · lasts until reset".
  - delta > 2: "N% over pace · runs out around <time>", where the run-out time is
    `now + (100 − used) / (used / elapsedMinutes)` minutes, rounded to 10 minutes. At or after the
    reset it reads "lasts until reset"; at 100% it reads "limit reached". Over-pace text is semibold
    orange.
- Extra usage: "Extra usage A$12.50 of A$100", plus "· off (out of credits)" when disabled.
- The cost block appears only for an account with logs.

## Settings

A standard toolbar preferences window: General, Accounts, About; fixed width, height fitted per tab;
title follows the tab; position remembered.

- **General.** Menu bar style with a live preview, metric switches, dropdown layout, refresh interval,
  Launch at login (off by default), the cost currency (AUD default; USD, EUR, GBP, NZD, CAD, JPY and
  the locale's currency), and, when converting, the exchange rate ("US$1 = A$1.42 · updated 26 Oct").
  JPY shows no decimals. Extra usage stays in the currency the API reports.
- **Accounts.** One section per account: name, tag, colour, sign-in source, status (green check with
  the email or organization, or the problem with its action button), show in menu bar, Remove
  Account… with confirmation. Add Account at the bottom. With no accounts: "No accounts", a line saying
  no Claude sign-in was found, and Add Account.
- **About.** Icon, name, version ("Version 0.2.0 (build N · hash)"), build date, signing identity,
  data sources, project folder (the repository root) with Show in Finder, and the authorship and
  licence lines.

## Cost scanner

Incremental per file, a `"usage"` pre-filter, de-duplication by `message.id:requestId` keeping the
largest record, buckets per local day and model, USD prices from `Pricing.swift`, runs off the main
thread at launch and every 5 minutes, cache file with counts only. See
[DATA-SOURCES.md](DATA-SOURCES.md) and [macos/ARCHITECTURE.md](macos/ARCHITECTURE.md).

## Build and install

macOS: `macos/scripts/install.sh` (checks, tests, build, sign, install). `macos/scripts/build.sh`
builds the bundle with bundle id `com.sipheren.usagebar`, `LSUIElement`, minimum macOS 26.0, the
version from `macos/VERSION`, and signs it with the configured identity (ad-hoc with a warning if
none).

## Not in this version

Other providers, notifications, widgets, a history of limits, Linux. A browser source on Windows (see
[PORTING.md](PORTING.md)).

## Security

Never print or log tokens or cookies. No token files. Keychain reads only. The cost cache holds counts
only.
