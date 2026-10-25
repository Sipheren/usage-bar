# Working rules for agents

Read this before changing anything in this repository. Background: [README.md](README.md),
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md), [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md),
[docs/DECISIONS.md](docs/DECISIONS.md).

## Scope

- Change only what the task names. Note anything else you spot instead of fixing it.
- If a task is ambiguous, stop and ask. A narrow, correct change beats a broad guess.
- Respect the decisions in `docs/DECISIONS.md`. Changing one needs the maintainer's say-so; record it
  as a new entry when it happens.

## Engineering

- Test first: write the failing test, watch it fail for the right reason, then make it pass. Run the
  whole suite (`swift test`) before you report.
- Logic goes in `UsageBarCore` with tests; `UsageBar` holds UI and wiring.
- Match the existing style: small files, comments only where the logic needs them, no new
  dependencies.
- Don't claim something works until you've run it. For UI, that means a real screenshot (see "Real-
  screenshot QA" in `docs/DEVELOPMENT.md`), not a preview.

## Secrets and privacy

- Never read, print or log a real token, cookie, Keychain value or token-cache value, in code, tests,
  scripts or your own commands. Tests use synthetic data. Debug output shows shapes, statuses and
  counts.
- Never commit personal data: names, emails, organization names, account or org ids, home-folder
  paths, hostnames, real usage or cost figures, screenshots with real data. `scripts/check-secrets.sh`
  must pass; the pre-commit hook runs it.
- Images for the docs come from demo mode (`--demo`). `screens/` stays untracked.
- Keep every source read-only: never refresh, write or delete another app's credentials or files.

## Running the app

- Install with `scripts/build.sh --replace` or `scripts/install.sh`, then check the change in the real
  app.
- Each usage request spends the account's rate-limit budget. Don't loop relaunches, Refresh Now or
  `--fetch-personal`. Use demo mode for anything visual.
- Don't change the bundle id `com.sipheren.usagebar`; macOS ties the user's permission grants to it.
- Close any Settings window or menu you opened, and delete temporary captures, before you finish.

## Releases

- Bump `VERSION` (SemVer) and add a `CHANGELOG.md` entry for user-visible changes. Add a dated line to
  `docs/DEVLOG.md`.
- Git: the maintainer handles commits and pushes. Don't run git unless you're asked to.
