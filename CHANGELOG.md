# Changelog

All notable changes to this project are recorded here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

**Platforms.** The entries so far are the macOS app. From now on each entry names its platform, for
example `## [macOS 0.3.0] - 2026-11-02` or `## [Windows 0.1.0] - …`. Each platform keeps its own
version (`macos/VERSION`, later `windows/VERSION`), and tags are `macos-vX.Y.Z` and `windows-vX.Y.Z`.
The existing tags `v0.1.0` and `v0.2.0` are macOS releases.

## [Unreleased]

### Added
- macOS: `--export-fixtures` and `--fixtures-expected` debug commands, which write anonymised API fixtures
  and an answer sheet for testing a port without real accounts (local data, never committed).

### Changed
- Repository (all platforms): `.gitattributes` marks `*.png` and `*.ico` as binary, for Windows checkouts.
- Docs: `docs/PORTING.md` records what the Windows build verified on a real machine; the README lists the
  Windows app.
- Repository (all platforms): reorganised for more than one platform: the macOS app moved to `macos/` (package,
  sources, tests, `VERSION`, build scripts), its architecture and development notes to `docs/macos/`,
  and the mockups to `design/mockup/`. Shared docs, design assets and repo-wide scripts stay at the
  root.

## [Windows 0.1.1] - 2026-10-26

### Changed
- Windows: tray icons show only an account's bars, or its session and weekly percentages as two numbers,
  in the account's colour; colour tells the accounts apart (decision 16). The tag badge is gone.
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
