# Devlog

> Built by AI (Claude, by Anthropic) under the direction of its author, Sipheren (sipheren@gmail.com).

## 2026-10-26 (Windows)

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
- Version 0.2.0, for friends: MIT licence, the authorship note above (also in the README and the
  About tab), a currency setting, and the menu bar spark as a vector trace instead of shipped PNGs.
- Reorganised the repository for a Windows port: the macOS app moved to `macos/`, shared docs and
  design assets stay at the root, `docs/PORTING.md` became the Windows starting brief and `AGENTS.md`
  the brief for agents on any platform. Per-platform versions and `macos-v` / `windows-v` tags.
