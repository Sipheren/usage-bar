# Working rules for agents

Read this before changing anything in this repository, on any platform.

## Layout

```
README.md, LICENSE, CHANGELOG.md, AGENTS.md, CLAUDE.md, .gitignore, .gitattributes, .githooks/
config/local.env.example   machine-local settings for any platform (copy to config/local.env, ignored)
scripts/                   repo-wide: setup-dev.sh, check-secrets.sh, secret-scan-allow.txt
design/                    icon/ (app icon), spark/ (ClaudeSpark.svg, trace_spark.py), mockup/
docs/                      shared: DATA-SOURCES.md, SPEC.md, DECISIONS.md, PORTING.md, DEVLOG.md, images/
docs/macos/                macOS only: ARCHITECTURE.md, DEVELOPMENT.md
macos/                     the macOS app: Package.swift, Sources/, Tests/, VERSION, scripts/, README.md
windows/                   the Windows app: UsageBar.slnx, Directory.Build.props, src/, tests/, VERSION, scripts/, README.md
```

Each platform lives in its own folder with its own `README.md`, `VERSION` and scripts. Shared rules
and assets stay outside the platform folders.

## Before you write code

- **macOS is the reference implementation.** When a doc and the macOS code disagree, the code wins;
  fix the doc.
- Read [docs/DATA-SOURCES.md](docs/DATA-SOURCES.md) (the cross-platform data contract) and
  [docs/PORTING.md](docs/PORTING.md) (what maps to Windows, what is still unverified). Then
  [docs/SPEC.md](docs/SPEC.md) and [docs/DECISIONS.md](docs/DECISIONS.md).
- macOS work: [docs/macos/ARCHITECTURE.md](docs/macos/ARCHITECTURE.md) and
  [docs/macos/DEVELOPMENT.md](docs/macos/DEVELOPMENT.md).

## Product rules (every platform)

- **Read-only.** Never refresh, write or delete another app's credentials, cookies or files. No
  sign-in flow of its own.
- **Data only from Anthropic** (the API and claude.ai), plus the exchange-rate API when costs aren't
  in US dollars. Nothing from CodexBar: no code, data or files (decision 1).
- **Monochrome menu bar / tray icon.** Usage never changes its colour (decision 4). On Windows the tray
  icons use each account's colour to tell the accounts apart (decision 17).
- **Bars** is the default label style; **dropdown A** (all accounts) is the default layout.
- **One or more accounts, up to one per sign-in source.** A first run adds only the sign-ins it finds
  (none is a valid state); saved settings are never given, or stripped of, accounts (decision 16).
- The tray and menu bar spark comes from `design/spark/ClaudeSpark.svg` (macOS draws the same shape
  from `macos/Sources/UsageBar/ClaudeSparkPath.swift`). The app icon source is `design/icon/`.
- Respect `docs/DECISIONS.md`. Changing a decision needs the maintainer's say-so; record it as a new
  entry when it happens.

## Engineering

- **Test first:** write the failing test, watch it fail for the right reason, then make it pass. Run
  the platform's whole suite before you report (macOS: `cd macos && swift test`).
- Keep logic in a testable core with synthetic-data tests; the UI layer holds UI and wiring.
- **Real-screenshot QA:** judge UI from real screenshots of the running app, not previews. Images you
  commit come from demo mode and go in `docs/images/`.
- Don't claim something works until you've run it.
- Match the existing style: small files, comments only where the logic needs them, no new
  dependencies without the maintainer's say-so.
- Change only what the task names. If a task is ambiguous, stop and ask.

## Privacy and secrets

- The only identifiers allowed in the repository are the handle **Sipheren**, the address
  **sipheren@gmail.com** and the bundle id **com.sipheren.usagebar**.
- Never the author's real name, any token, password, cookie or key, or other personal data: other
  emails, organization names, account or org ids, home-folder paths, hostnames, real usage or cost
  figures, screenshots with real data.
- Never read, print or log a real token, cookie or credential value, in code, tests, scripts or your
  own commands. Debug output shows shapes, statuses and counts.
- `scripts/check-secrets.sh` must pass; the pre-commit hook runs it on staged files. `screens/` (real
  captures) stays untracked.

## On a new machine

1. Run `scripts/setup-dev.sh` from the repository (on Windows, in Git Bash). It sets the git hooks and
   creates `config/local.env` from `config/local.env.example`.
2. Edit `config/local.env`: put that machine's private words (real name, employer, hostname, account
   id prefixes) in `SECRET_SCAN_PRIVATE_PATTERNS`, and on a Mac the signing identity. The file is
   ignored, so the list itself is never committed.

## Running the app

- Each usage request spends the account's rate-limit budget. Don't loop relaunches, Refresh Now or
  real-request debug commands. Use demo mode for anything visual.
- macOS: install with `macos/scripts/install.sh` (or `macos/scripts/build.sh --replace`). Don't change
  the bundle id; macOS ties the user's permission grants to it.
- Close any settings window or menu you opened, and delete temporary captures, before you finish.

## Versions, tags and the changelog

- Each platform has its own version file: `macos/VERSION` and `windows/VERSION`. Use SemVer.
- Tags name the platform: `macos-vX.Y.Z`, `windows-vX.Y.Z`. The existing `v0.1.0` and `v0.2.0` tags are
  the macOS app.
- Every `CHANGELOG.md` entry names its platform, for example `## [macOS 0.3.0] - 2026-11-02`. Entries
  before this rule are the macOS app. Add a dated line to `docs/DEVLOG.md` for notable work.
- Git: the maintainer handles commits, tags and pushes. Don't run git unless you're asked to.
- Cutting a release: once the tag is pushed and the files in `scripts/release/` are committed, run
  `scripts/release.sh <platform>` to build and check the zip in `dist/`, then
  `scripts/release.sh <platform> --publish` to upload it as a draft GitHub release. The maintainer
  publishes the draft.
