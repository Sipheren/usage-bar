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
- Repository (all platforms): reorganised for more than one platform: the macOS app moved to `macos/` (package,
  sources, tests, `VERSION`, build scripts), its architecture and development notes to `docs/macos/`,
  and the mockups to `design/mockup/`. Shared docs, design assets and repo-wide scripts stay at the
  root.

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
