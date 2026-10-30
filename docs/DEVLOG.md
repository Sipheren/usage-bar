# Devlog

## 2026-10-30 (Windows 0.1.8)

- Ported macOS 0.3.3: the "… is signed in to your Personal account" check is gone on Windows too. No
  `SignedInToPersonal` issue or cookie outcome, no `IsPersonalOrg`, no organization from `~/.claude.json`
  in the org pick, and no Claude app "is this the Claude Code account" block or post-200 organization check.
  `~/.claude.json` still gives the Claude Code account's email and first-run detection; the first run's
  rule (skip a Claude app on the Claude Code account) is unchanged pending the maintainer's call.
- The Claude app's token probe moved into Core (`DesktopTokenParser.ProbeAsync`) so it can be tested: the
  token for the app's own account wins, else the first whose profile answers 200, latest expiry first.
- Test-first: `SignedInAccountTests.cs` (4 probe tests) and the cookie client's org pick and full flow
  (first org, `lastActiveOrg`, a single organization always shown). 229 tests, none skipped with the
  fixtures folder. The parity test no longer reads `personalOrg`/`isPersonal`; the local fixtures have one
  organization, so the pick is unchanged.
- Installed over 0.1.7: started, skipped the launch check (inside 15 minutes), then `ClaudeCode: ok, HTTP
  200, 3 limits, route token`. This PC has no Claude app account, so the Claude app routes are covered by
  tests only.

## 2026-10-30 (macOS 0.3.3)

- Safari and the Claude app showed "… is signed in to your Personal account" and no numbers once
  `~/.claude.json` came to name the organization they were signed in to. The app decided which account
  was "Personal" from that file; the account's sign-in source in Settings is what decides what it shows.
  The check is gone: no `signedInToPersonal`, no `isPersonalOrg`, no organization from `~/.claude.json`
  in the org pick or the Claude app's token choice. `~/.claude.json` still gives the Claude Code
  account's email and first-run detection.
- Test-first in `SignedInAccountTests.swift`, with a stub `URLProtocol` and a `~/.claude.json` naming
  the same account and organization as the session: Safari, the Claude app's cookie route and its token
  choice all show numbers. `AccountFetcher` takes its HTTP session and account-file path from tests.
- The parity sheet drops `personalOrg` and `isPersonal`; a local fixtures folder needs
  `UsageBar --fixtures-expected DIR` again. Windows (0.1.7) is unchanged and still has the check.
- Installed over 0.3.2 on this Mac: the Safari account, which had logged `signedInToPersonal, HTTP 200`
  every 2 minutes, logged `ok, HTTP 200, 3 limits, route safari` on the next two checks. No Claude app
  account is set up on this Mac, so its route is covered by tests only.

## 2026-10-28 (Windows 0.1.7)

- Ported macOS 0.3.2 (decision 19), test-first from `RateLimitTests.swift`: the 15-minute floor, the
  flyout and Refresh Now rules, the jitter with an injected generator (`Func<ulong>`, one draw), backoff
  doubling, its cap and step-down, the next-check time, staleness by schedule, the new header notes and
  the state file old and new (27 new tests, 226 in all, none skipped with the fixtures folder). The
  local fixtures' answer sheet was 0.3.0's; the Mac sent 0.3.2's one changed field
  (`rateLimitedWithNumbers`), and the parity test passes against it.
- `PersistedAccountState` gains `Backoff` and `LastRoute` as optional constructor parameters, so a 0.1.6
  `state.json` still loads (tested with 0.1.6's date format). The cost scan and the rate check keep the
  refresh setting's pace, as in 0.1.6.
- Installed over 0.1.6 on this PC: the launch logged "not fetched at launch, next due 3:30 pm (every 15
  min)"; opening the flyout 4 minutes after a check didn't fetch; Refresh Now logged "Refresh Now
  skipped, next check 3:30 pm" and the account header showed "Next check 3:30 pm"; the timer then
  fetched 14 min 45 s after the previous check, and `state.json` gained `backoff` and `lastRoute`. No
  429 came during the check, so the jitter, backoff and the 0.1.6-wait upgrade are covered by tests only.

## 2026-10-28 (macOS 0.3.2)

- The Claude Code account still got 429s on 0.3.1: Retry-After 3471 s, then a retry on the boundary
  that got 3600 s, one good read, and another 429 (3402 s) on the next 5-minute check. Claude Code's own
  `/usage` and other pollers share the endpoint, and Anthropic publishes no limits for it (decision 19
  has the reports). The "about 5 requests every few minutes" in the docs was our guess; corrected.
- Token sign-ins now poll every 15 minutes at most, never on a menu open, and Refresh Now needs a
  5-minute gap. Every 429 adds 2 to 10 minutes of jitter and doubles the account's interval (to an hour
  at most); 3 good reads step it back. Cookie sign-ins keep their setting.
- `state.json` now also holds the backoff and the last route, as optional keys so 0.3.1's file loads. A
  wait 0.3.1 saved ends exactly on the boundary, so the first launch adds the jitter and counts it as one
  429. Installing over 0.3.1 inside a wait logged "wait saved by an earlier version" and "not fetched at
  launch, rate-limit wait until …" for the Claude Code account, and the Safari account kept fetching.
- Layout A's single "Updated N min ago" showed the newest account's age; each account now has its own.
- Test-first in `RateLimitTests.swift`: the 15-minute floor, the menu and Refresh Now rules, the jitter
  with an injected generator, backoff doubling, its cap and step-down, and the state file old and new.
  `Double.random(in:using:)` rejects draws, so a constant test generator hung it; the jitter takes one
  draw instead.
- Follow-ups from the first real screenshot: the flat 15-minute greying made a healthy Claude Code
  account look broken between its 15 to 60-minute checks, so staleness now follows the account's own
  schedule and wait (`MenuBarModel.isStale`). "Paused by rate limit · retrying 3:02 pm" wrapped with the
  time alone on the next line; it is now "Rate limited · next check 3:02 pm" with no-break spaces. A
  skipped Refresh Now or Connect sets a "Next check" note in the account header.

## 2026-10-27 (source releases)

- `scripts/release.sh` builds one source zip per platform with `git archive` from HEAD, so untracked
  files can't get in, and refuses unless the app's files match the platform's tag. `--publish` uploads
  the zip and its notes (the changelog section and the install steps) as a draft GitHub release. The
  Windows zip carries `design/icon` and `design/spark`: `UsageBar.csproj` builds the tray spark and the
  icon layers from them.

## 2026-10-27 (Windows 0.1.6)

- Ported macOS 0.3.1's rate-limit fixes (decision 18), test-first: the timer, launch and flyout rules,
  the 5-minute floor for token sign-ins, `RateLimitWait`, the claude.ai 429 (end to end through a stubbed
  HTTP client) and the table of Claude app cookie results that stand or try the token (19 new tests, two
  updated). The three timer tests were run against the old rule first and failed.
- The cost scan and the rate check stay at the refresh setting's pace; only the account fetches moved to
  the 60-second tick.
- The cookie-first order in `DesktopSource` is covered only through its decision function. On this PC the
  Claude app is on the Claude Code account, so it stops before either route.

## 2026-10-27 (macOS 0.3.1)

- The Claude Code account kept hitting 429. Its OAuth usage endpoint allows only about 5 requests every
  few minutes, shared with Claude Code's own `/usage`, and the app polled every 2 minutes. Anthropic
  often sends `Retry-After: 0`, which the app read as "no wait": the log showed "retrying at 6:17 pm"
  written at 18:17. A 429 now waits 5 minutes when Retry-After is missing or 0, and at most an hour.
- Each account has its own background interval: the setting, but at least 5 minutes on the OAuth
  endpoint. A 60-second tick fetches whatever is due; opening the menu fetches numbers 2 minutes old.
  claude.ai's endpoint has its own allowance, so cookie sign-ins keep the setting.
- The Claude app tries its claude.ai cookie first and its token only when the cookie doesn't work. A
  claude.ai 429 now pauses the account instead of showing "HTTP 429". The token route asks for the
  profile only when it needs to.
- Test-first in `RateLimitTests.swift` (18 new tests). The fetch flows have no stubbed network on macOS,
  so the cookie-first order is covered only through its decision function, not end to end.

## 2026-10-26 (macOS 0.3.0)

- Accounts are one or more, up to one per sign-in source; a first run adds only the sign-ins it finds
  (the Claude Code Keychain item exists; the Claude app is on a different account), and zero is a normal
  state with its own dropdown and Settings views.
- The authorship note moved to one line at the end of the README, and About's footer became one short line.
- From the cross-platform review: the Sign-in source picker greys out a source another account uses, and
  an account a build can't read is kept and saved back instead of resetting the list (11 new tests).

## 2026-10-26 (Windows)

- 0.1.5: fixes from the Mac-side review, test-first where Core could hold them (25 new test cases, including a
  stub-HTTP suite for the usage client and fetcher). Checked on this PC: Claude Code writes `.claude.json`
  inside a `CLAUDE_CONFIG_DIR` folder and leaves `~/.claude.json` alone. With the new sharing a reader
  lets the owner rewrite, rename away or delete the file; a rename *over* an open file still fails on
  Windows whatever the reader shares, so that case relies on reads being brief. Damaged settings files
  and a forced startup failure were launched for real: the app starts or exits, never lingers.
- 0.1.4: Windows follows the new account contract (decision 16), ported test-first from the Mac's
  `AccountSetupTests`. Detection was checked against this PC's real files (`--detect-sign-ins`: the Claude
  app is on the Claude Code account, so only Personal is added) and with a real first run. That run showed
  0.1.0 to 0.1.3 never saved settings on their own: `state.json` held 16 ids for one account, one per
  launch. Interaction checks through UI Automation also caught demo mode reading the real Claude app
  config when an account was added, and an unthemed Add Account menu.
- 0.1.3: a bug pass before merging. `--selftest` showed every closed Settings window that had shown the
  Accounts tab staying alive; removing the account card's controls one type at a time pointed at the
  colour combo, and freezing its swatch brushes fixed it (0 live windows and flat memory over three
  rounds). An independent review found the rest: the flyout edge came from the work area, a click on
  another icon only closed the flyout, writes caught IOException only, and a theme switch left title
  bars stale. Eight-account captures (`--scenario edge`) found the long-name, tab and off-screen
  Settings layouts. A 25-minute idle soak of the installed app stayed flat (75–90 MB private,
  1,190–1,220 handles, 36–54 GDI objects).
- 0.1.2: account colours became ten tested presets after the maintainer saw orange render as red in the
  tray. Each preset's dark and light shades were picked by measured contrast and checked on a rendered
  sheet (`--colour-sheet`), on the real taskbar and in the flyout with lossless captures.
- 0.1.1: the maintainer's calls after using it. Tray icons are just bars or numbers in the account's
  colour (decision 17), checked at 8× in the overflow and on the taskbar. One account by default: an
  account appears only when there is a sign-in behind it; the fixtures add a test account that's never
  saved.
- Started the Windows port in `windows/`: C# on .NET 10 with WPF, the core ported test-first from the
  macOS core, xUnit tests mirroring `macos/Tests`, and parity tests that reproduce `expected.json` from the
  shared fixtures (all pass).
- Previewed the look as a design canvas next to the Mac screenshots, checked each board from real
  renders, then built the tray app to match: per-account tray icons (the maintainer's choice, with a single
  icon as an option), the flyout in both layouts on acrylic, the right-click menu and Settings.
- Checked every view from real screenshots of the running app (lossless captures where colour mattered).
  Fixes found that way: a DWM close button drawn over the flyout, translucent black vanishing over the
  acrylic in the light theme, the flyout growing off the bottom of the screen, a style key clashing with a
  colour key, unreadable 8 px tag badges, switches that ignored non-mouse toggles.
- Verified on Windows 11: the Claude Code credentials file shape, `.claude.json`, the logs folder, the
  Claude app's DPAPI and AES-256-GCM token cache (key names and types only), the tray overflow default and
  the 20 px tray icon size at 125% scaling. Recorded in `docs/PORTING.md`.

## 2026-10-25

- Built the first version from the spec: SwiftPM package with a Core library, the app and tests.
- Usage decoding (`limits[]` with a `five_hour`/`seven_day` fallback), pace maths, reset text, menu bar
  styles, Dropdown A and B, Settings, Launch at login.
- Claude Code source through `/usr/bin/security`; Claude app source with Chromium `safeStorage`
  decryption, a lenient token-cache parser and `--dump-desktop-shape`.
- Cost scanner over Claude Code logs: incremental, de-duplicated, priced per model. Checked its totals
  against an independent script; they matched. Fixed a memory leak in the scan's worker threads.
- Coordinator corrections: keep the largest record per message, cache-write split rules, a ±2-point
  "on pace" band, 429 handling, the CLI User-Agent.
- Swapped the hand-drawn spark for the Claude tray glyph; spaced the slash in the Percentages style.
- Added the Safari source (binarycookies parser, claude.ai organizations and usage, Cloudflare
  detection), a per-account sign-in source picker, an About tab and an app icon.
- Review fixes: strict PKCS7 so a wrong key fails, the Safe Storage account name, cached-choice rules,
  never showing the personal account under Work, a 10-second Keychain timeout, scope errors, scoped
  limits with the same name.
- Made the menu bar monochrome and Bars the default.

## 2026-10-26

- Rebuilt Settings as a standard toolbar preferences window: General, Accounts and About tabs, fixed
  width, height fitted to each tab, no initial focus.
- Dropdown polish: real gap between bar and pace line, stronger track, "starts on first use", scoped
  rows use the normal pace logic, icons on every native menu item (macOS 27 hides them by default).
- Moved the app icon to an Icon Composer document compiled by `actool`, which removed the grey frame.
- Found that narrowing the Safari search broke the Work source, and restored the broader sweep.
- Reset times now round to the nearest minute (the API sends 14:59:59.6 for 15:00).
- Rate-limit budget: launches and menu opens fetch only when due, per-account state persists across
  launches, a 429 keeps the numbers and notes the pause, every 429 is logged.
- Project packaging: VERSION and CHANGELOG, configurable signing, install/uninstall/check-secrets/
  setup-dev scripts, a pre-commit hook, unified logging, demo mode, documentation.
- Fixed a redraw loop that kept a CPU core busy: the status item observed its own appearance and set a
  new image each time, and each new image fired the observation again. The label is a template image,
  so the observer was removed.
- Made `--close-menu` work while the menu is open (a Darwin notification polled during menu tracking).
- Version 0.2.0, for friends: MIT licence, an authorship note (README and About tab), a currency setting, and the menu bar spark as a vector trace instead of shipped PNGs.
- Reorganised the repository for a Windows port: the macOS app moved to `macos/`, shared docs and
  design assets stay at the root, `docs/PORTING.md` became the Windows starting brief and `AGENTS.md`
  the brief for agents on any platform. Per-platform versions and `macos-v` / `windows-v` tags.
- Any number of accounts, up to one per sign-in source: a first run adds only the sign-ins it finds
  (Claude Code by its Keychain item's attributes, the Claude app from its config.json, never Safari),
  zero accounts has a proper state in the dropdown and Settings, Layout B drops its tabs for one
  account, and the menu bar is the spark alone with none shown. Checked in demo mode at 0 to 3 accounts
  with the new `--demo-accounts` and `--set-style` hooks.
