# Changelog

All notable changes to this project are recorded here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

**Platforms.** The entries so far are the macOS app. From now on each entry names its platform, for
example `## [macOS 0.3.0] - 2026-11-02` or `## [Windows 0.1.0] - …`. Each platform keeps its own
version (`macos/VERSION`, `windows/VERSION`), and tags are `macos-vX.Y.Z` and `windows-vX.Y.Z`.
The existing tags `v0.1.0` and `v0.2.0` are macOS releases.

## [Unreleased]

### Added
- macOS: `--export-fixtures` and `--fixtures-expected` debug commands, which write anonymised API fixtures
  and an answer sheet for testing a port without real accounts (local data, never committed).
- macOS: a "No accounts" state in the dropdown (both layouts, with **Open Settings…**, which opens the
  Accounts tab) and in Settings > Accounts.
- macOS: debug commands `--demo-accounts N` (demo mode with 0 to 3 accounts), `--detect-sign-ins` (what
  a first run would add) and `--set-style` (switch the menu bar style).

### Changed
- Repository (all platforms): `.gitattributes` marks `*.png` and `*.ico` as binary, for Windows checkouts.
- Docs: `docs/PORTING.md` records what the Windows build verified on a real machine; the README lists the
  Windows app (0.1.4) and its tray icons.
- Docs: the authorship note is one line at the end of the README instead of a quote at the top of the
  README and the devlog.
- macOS: the About credit is now a short footer line ("MIT License. Made with Claude by Sipheren.").
- Windows: the About credit is now a short footer line ("MIT License. Made with Claude by Sipheren.").
- Repository (all platforms): reorganised for more than one platform: the macOS app moved to `macos/` (package,
  sources, tests, `VERSION`, build scripts), its architecture and development notes to `docs/macos/`,
  and the mockups to `design/mockup/`. Shared docs, design assets and repo-wide scripts stay at the
  root.
- All platforms (docs): the contract is now one or more accounts, up to one per sign-in source, and a
  first run adds only what's signed in (`docs/SPEC.md`, `docs/DATA-SOURCES.md` "First-run detection",
  decision 16).
- macOS: a fresh install adds only the sign-ins it finds, with no prompt: Personal when the Claude Code
  login's Keychain item exists (checked without reading it), and a Claude app account when the app's
  `config.json` shows a sign-in to a different account. Safari is never probed on first run; add it from
  Settings. Existing saved settings load unchanged.
- macOS: the dropdown layout "Both accounts" is now "All accounts" (the saved value is unchanged).
- macOS: Layout B hides its tab strip with a single account, and tab names truncate cleanly.
- macOS: a new Claude app account is violet (`#8E6AC8`), so three accounts never share a colour.

### Fixed
- macOS: a fresh install without a Safari sign-in no longer shows an empty Work account.
- macOS: with no account in the menu bar, the label's tooltip and accessibility text were blank; they
  now read "Claude usage", and the label is the spark alone.

## [Windows 0.1.5] - 2026-10-26

Fixes from the Mac-side review of 0.1.4 (all edge cases).

### Fixed
- Windows: a startup error no longer leaves an invisible copy running that holds the single-instance lock
  and swallows every later launch; it logs the error type and exits.
- Windows: a damaged `settings.json` can't stop the app or lose accounts. A null account is dropped; an
  account this build can't read (no source, an unknown source kind, a bad id) is set aside and written
  back unchanged, and the others load with their ids. Only a file without an account list falls back to
  Personal. A `settings.json` that isn't JSON at all is copied to `settings.unreadable.json` before the
  fresh start replaces it.
- Windows: reading Claude Code's and the Claude app's files no longer blocks them from rewriting, renaming
  or deleting those files.
- Windows: a login file that can't be read (locked, path too long) or holds an out-of-range expiry shows
  "Couldn't read the Claude Code login" instead of silently doing nothing; any other fetch failure shows
  "Couldn't check this account". A huge Retry-After is capped at a day. One account's cost logs failing no
  longer stops the others.
- Windows: with `CLAUDE_CONFIG_DIR` set, the account file is read from inside that folder everywhere (first
  run, the Claude app's same-account check, fixtures), as Claude Code writes it there.
- Windows: "Paused by rate limit" comes back after a relaunch while the pause lasts.
- Windows: an account's Sign-in source picker greys out a source another account uses.
- Windows: a blanked name or tag goes back to the saved one when you leave the box.
- Windows: changing an account's source while it's fetching no longer runs two requests at once.
- Windows: the Claude app's cookie fallback works from a user folder with `%`, `#` or `?` in its name.
- Windows: the Claude Code version in requests is read each time, so an update shows without a restart.
- Windows: Launch at login shows On for any Usage Bar entry, so one left by another copy can be turned off.
- Windows: a light/dark switch redraws an open flyout, the Settings preview and the colour swatches.
- Windows: absurd server values (`decimal_places` of 29, a far-future rate time) no longer throw.
- Windows: `install.ps1` builds before stopping anything (a failed build leaves the running copy alone),
  stops only the installed copy, installs into a clean folder, and says so plainly when `dotnet` is missing.

## [Windows 0.1.4] - 2026-10-26

### Changed
- Windows: the first launch adds an account only for each sign-in it finds, with no prompt and nothing
  decrypted: Personal when Claude Code's credentials file exists, and a Claude app account when the app is
  signed in to a different account than Claude Code. Nothing found means no accounts (decision 16).
- Windows: saved settings load unchanged. 0.1.1's one-time removal of an unused default Work account is
  gone.
- Windows: Add Account offers each sign-in source once (a source in use is greyed out) and adds that
  source's account: Personal, or Claude app in purple.
- Windows: the "Both accounts" layout is now "All accounts" (the saved value is unchanged).

### Added
- Windows: a "No accounts" state in the flyout, whose **Open Settings…** opens the Accounts tab, and in
  Settings > Accounts.
- Windows: debug options `--demo-accounts 0|1|2` and `--detect-sign-ins <file>`.

### Fixed
- Windows: settings weren't saved until you changed one, so every launch gave the account a new id: a new
  tray-icon entry in Windows' list each time, and saved usage that was never reused. The first run now
  saves straight away, and saved state for accounts that no longer exist is dropped.
- Windows: demo mode no longer reads or fetches anything when an account is added or switched, or the
  currency changes.
- Windows: context menus (Add Account, text-box edit menus) follow the app's light or dark palette, and the
  Add Account buttons have an accessible name.

## [Windows 0.1.3] - 2026-10-26

### Fixed
- Windows: a closed Settings window stayed in memory after its Accounts tab was shown (about 7 MB and 40
  GDI objects each time). The colour swatches used unfrozen brushes, which kept the window reachable.
- Windows: the flyout finds the taskbar from Windows instead of guessing from the work area, so it opens
  beside the taskbar when the taskbar auto-hides or an app bar is docked.
- Windows: with many accounts the flyout scrolls inside the screen height, and the menu rows stay below.
- Windows: clicking another account's tray icon while the flyout is open moves the flyout to it instead of
  only closing it.
- Windows: open windows restyle their title bar and backdrop when Windows switches light or dark.
- Windows: a long account name wraps instead of running into the cost, and the One at a time title wraps
  instead of running under the refresh button. With more than four accounts the tabs show badges only,
  with the name as a tooltip. The badge and cost line up with the name's first line, as on the Mac.
- Windows: Settings moves up to stay on screen when a taller tab opens.
- Windows: a settings or cache file that can't be written (access denied) is logged instead of closing the
  app. Any other unexpected error is logged by type only.
- Windows: the tray icon size follows the main display's current scale, not the scale at sign-in.

### Added
- Windows: `--selftest <report>` (a resource-leak run) and `--scenario edge` (eight awkward accounts)
  for testing; the colour sheet also shows the icons at 16, 24 and 32 px.

## [Windows 0.1.2] - 2026-10-26

### Changed
- Windows: account colours are ten presets (Orange, Blue, Green, Purple, Pink, Teal, Yellow, Red, Lime,
  Grey), each with a shade for dark and for light taskbars and a badge shade. The tray and the flyout use
  the same shade, so the colour you pick is the colour you see. Tests hold every preset to measured contrast
  and keep them distinct. Colours saved by 0.1.x map to the preset of the same hue (clay stays orange).
- Windows: the tray bars sit on a neutral track; a track tinted with the account colour made orange read
  as red.
- Windows: the colour picker in Settings lists the presets by name with a swatch.

## [Windows 0.1.1] - 2026-10-26

### Changed
- Windows: tray icons show only an account's bars, or its session and weekly percentages as two numbers,
  in the account's colour; colour tells the accounts apart (decision 17). The tag badge is gone.
- Windows: one account by default (Personal, on the Claude Code login). Others are added in Settings, so no
  account appears without a sign-in behind it. Settings saved by 0.1.0 drop the untouched default Work
  account.
- Windows: `--fixtures` adds a Work test account that is never saved, instead of switching a saved account.
- Windows: the one-at-a-time flyout hides the account tabs when there is only one account.

## [Windows 0.1.0] - 2026-10-26

The first Windows version, in `windows/` (C# on .NET 10 with WPF). Tags for it are `windows-vX.Y.Z`.

### Added
- Windows: notification-area icons, one per account by default (a tag badge over the account's bars or
  session percentage) or a single icon (bars, percentage or the spark), drawn at the real pixel size,
  white on a dark taskbar and black on a light one; tooltips with each limit and its reset.
- Windows: a flyout with both layouts (both accounts, the default, or one at a time with the 30-day chart)
  on Windows 11 acrylic, anchored above the tray; a right-click menu with Refresh Now, Settings…, About and
  Quit.
- Windows: Settings with General, Accounts and About, as on the Mac: tray style and icons, limits, layout,
  refresh interval, launch at login (the per-user Run key, off by default) and currency; account names,
  tags, colours, sign-in sources, add and remove.
- Windows: the Claude Code source (`.credentials.json`), the Claude app source (DPAPI and AES-256-GCM,
  read only after Connect…, with the cookie fallback), the cost estimate from Claude Code logs, exchange
  rates, persisted fetch state and rate-limit handling, all ported from the macOS core with its tests.
- Windows: parity tests that reproduce the macOS answer sheet from a fixtures folder, a fixture source
  (`--fixtures`) for testing the Work account without a work sign-in, demo mode (`--demo`), and
  `install.ps1` / `uninstall.ps1`.

## [0.2.0] - 2026-10-26

### Added
- Currency setting for cost figures: AUD (default), USD, EUR, GBP, NZD, CAD, JPY, plus your locale's
  currency when the rate list has it. USD needs no exchange rate and makes no rate request.
- MIT licence.
- Authorship note in the README and the About tab.

### Changed
- Menu bar icon: vector trace of the Claude spark, drawn from a path in code. The app no longer ships
  the Claude app's tray icon PNGs.

### Fixed
- Memory held after switching Settings tabs. Each switch left SwiftUI observation-tracking registrations
  behind (about 130 KB), and nothing released them until the app quit. The Settings window is now torn down
  when it closes and rebuilt on the next open. Measured: 32 MB with the window open, back to 13 MB after
  closing, flat across rounds.

## [0.1.0] - 2026-10-26

### Added
- Menu bar app showing Claude plan limits (5-hour session, weekly, model weekly) for two accounts.
- Sign-in sources: Claude Code login, Claude app login, Safari session. All read-only.
- Menu bar styles: Bars (default), Percentages, Compact, Icon only. Monochrome label.
- Dropdown layouts: both accounts (default) and one at a time, with pace markers and pace lines.
- Cost estimate from Claude Code logs at API prices, in AUD, with a 30-day chart.
- Extra usage display.
- Settings window with General, Accounts and About tabs; Launch at login.
- Rate-limit handling: Retry-After, kept numbers during a pause, no refetch on relaunch when not due.
- Demo mode (`--demo`) with sample data.
- Unified logging (subsystem `com.sipheren.usagebar`).
- Scripts: install, uninstall, build, check-secrets, setup-dev, screenshot QA; pre-commit hook.
