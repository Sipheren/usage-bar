# Changelog

All notable changes to this project are recorded here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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
