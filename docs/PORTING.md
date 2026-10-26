# Porting: the Windows starting brief

The Windows port hasn't started. It goes in `windows/` at the repository root; create that folder with
its own `README.md` and `VERSION`. The macOS app in `macos/` is the reference implementation: when this
page and the macOS code disagree, the code is right and this page needs fixing.

Read these first: [AGENTS.md](../AGENTS.md) (rules), [DATA-SOURCES.md](DATA-SOURCES.md) (the data
contract), [SPEC.md](SPEC.md) (behaviour), [DECISIONS.md](DECISIONS.md) (choices you keep).

Status markers below: **verified** means checked against the cited source; **unverified** means
nobody has confirmed it on a Windows machine yet. Confirm every unverified item before relying on it,
then update this page.

## Data sources on Windows

### Claude Code login

- **Credentials:** `%USERPROFILE%\.claude\.credentials.json`, or `.credentials.json` in
  `%CLAUDE_CONFIG_DIR%` when that variable is set. The file inherits the user profile's access
  controls. **Verified** against the Claude Code docs, "Credential management":
  <https://code.claude.com/docs/en/iam>.
- **JSON shape: unverified.** Expected to be the same payload macOS keeps in the Keychain:
  `claudeAiOauth.accessToken` and `claudeAiOauth.expiresAt` (milliseconds since 1970).
- **Account info:** `%USERPROFILE%\.claude.json`, field `oauthAccount`. **Unverified.**
- **Logs for the cost estimate:** `%USERPROFILE%\.claude\projects\**\*.jsonl`. **Unverified** (the docs
  say each config directory holds its own session history; the folder name is from macOS).
- Read the file; never write, refresh or delete it. Claude Code manages it through `/login` and
  `/logout`. Expired token: show "Claude Code login expired — run `claude` once" and make no request.
- No permission prompt is involved, as on macOS.

### Claude desktop app login

- **What protects it:** Electron `safeStorage`, which uses DPAPI on Windows. **Verified** against the
  Electron docs: <https://www.electronjs.org/docs/latest/api/safe-storage>.
- **Chromium's Windows scheme** (Electron's `safeStorage` is built on it): the key sits in the app's
  `Local State` JSON at `os_crypt.encrypted_key`, base64 of the bytes `DPAPI` plus a DPAPI blob.
  `CryptUnprotectData` returns a 32-byte AES-256 key. Each value is `v10`, a 12-byte nonce, the
  ciphertext and a 16-byte GCM tag. **Verified** against Chromium's `os_crypt_win.cc`:
  <https://chromium.googlesource.com/chromium/src.git/+/main/components/os_crypt/sync/os_crypt_win.cc>.
- **The trap: the same `v10` prefix means a different cipher.** On macOS `v10` is AES-128-CBC with a
  PBKDF2 key from the Keychain (`ChromiumCrypto.swift`). On Windows it is AES-256-GCM with the DPAPI
  key above. Port `DesktopTokenParser.swift` (finding and choosing tokens), not the CBC decryption.
- **Unverified:**
  - that the Claude app on Windows encrypts `oauth:tokenCacheV2` / `oauth:tokenCache` this way;
  - where its `config.json` and `Local State` live (probably `%APPDATA%\Claude\`);
  - where its cookie database lives for the `sessionKey` fallback (newer Chromium uses
    `Network\Cookies` under the profile).
- DPAPI decrypts silently for the same Windows user, so there is no prompt like macOS's Keychain
  "Always Allow". Keep the Connect… step anyway (decision 12): the app shouldn't read another app's
  sign-in until the user asks it to.

### Browser session (Safari on macOS)

- Safari doesn't exist on Windows, so this source needs a browser to replace it.
- **Chrome:** since Chrome 127, cookies on Windows use app-bound encryption (values prefixed `v20`),
  which only Chrome's own elevated service will decrypt. **Verified** against Google's announcement:
  <https://security.googleblog.com/2024/07/improving-security-of-chrome-cookies-on.html>. Don't build
  a Chrome cookie source.
- **Edge:** Chromium-based. **Unverified** whether it uses app-bound encryption too.
- **Firefox:** `cookies.sqlite` in the profile folder, with values not encrypted. **Unverified** on
  current versions. Firefox holds the database open while it runs; open it with SQLite's `immutable=1`
URI flag, as the macOS reader does for the Claude app's cookies, and never copy it to disk.
- Practical order: build the Claude Code source first, then the Claude app source. Leave a browser
  source until the other two work.

### First run

Follow [DATA-SOURCES.md](DATA-SOURCES.md#first-run-detection): one or more accounts, up to one per
sign-in source, and a first run adds only the sign-ins it finds, with no prompt and nothing decrypted.
Zero accounts is a normal state. On Windows (all **unverified**):

- Claude Code: the `.credentials.json` above exists. Don't parse the token to decide.
- Claude app: its `config.json` has a non-empty `oauth:tokenCacheV2` and a `lastKnownAccountUuid` that
  isn't `oauthAccount.accountUuid` in `%USERPROFILE%\.claude.json`. Don't call DPAPI for this check.
- Browser session: never checked on first run; the user adds it from Settings.

### Everything else is the same

The endpoints, headers, response decoding, 429 handling, log rules and prices in
[DATA-SOURCES.md](DATA-SOURCES.md) apply unchanged. The usage request sends the same
`User-Agent: claude-cli/<version> (external, cli)`.

## The tray: Windows' equivalent of the menu bar

- **Icon:** a notification-area icon. Windows draws tray icons at the small-icon size: 16 px at 100%
  scaling, larger at higher DPI. Ask `GetSystemMetrics(SM_CXSMICON)` (per monitor DPI) instead of
  assuming 16.
- **No text label.** The macOS label (spark, tags, bars, percentages) doesn't fit in a tray icon. The
  icon shows the spark from `design/spark/ClaudeSpark.svg`, or bars for the first account in the Bars
  style. The tooltip carries the numbers (`P 20% / 60% · W 45% / 72%`), and the classic tooltip limit
  is 128 characters.
- **Monochrome:** there are no template images, so draw white on a dark taskbar and black on a light
  one. The usual signal is `SystemUsesLightTheme` under
  `HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize` (**unverified**, not a
  documented API); redraw when the theme changes.
- **Hidden by default (unverified):** Windows 11 puts new tray icons in the overflow until the user
  turns them on in Settings > Personalization > Taskbar. Say so in `windows/README.md`.
- **Dropdown:** a borderless flyout on left-click, anchored to the icon (`Shell_NotifyIconGetRect`),
  closing when it loses focus. It holds Dropdown A by default, Layout B as a setting. Right-click
  opens a menu: Refresh Now, Settings…, About Usage Bar, Quit.
- **Settings:** a normal window with General, Accounts and About tabs, as on macOS.
- **Launch at login:** off by default; the per-user `Run` key or a Startup folder shortcut.
- **App data:** `%APPDATA%\UsageBar` for settings and state, `%LOCALAPPDATA%\UsageBar` for the cost
  cache and logs. The logging rules are the same: statuses and counts, never tokens, cookies, emails or
  organization names.
- **App icon:** build an `.ico` from the layers in `design/icon/AppIcon.icon/Assets/` (drawn by
  `design/icon/make_icon.py`).

## Pure logic to port, and the tests to mirror

Port these with their tests before any UI. All the macOS tests use synthetic data, and the synthetic
ids in `scripts/secret-scan-allow.txt` can be reused.

| Logic | macOS source (`macos/Sources/UsageBarCore/`) | Tests to mirror (`macos/Tests/UsageBarTests/`) |
|---|---|---|
| Usage decoding: `limits[]` first, `five_hour`/`seven_day` fallback, fractional seconds, lenient | `Usage.swift` | `UsageDecodingTests.swift` |
| Pace maths and pace sentences | `Pace.swift`, `LimitDisplay.swift` | `PaceTests` in `PaceAndFormattingTests.swift`, `LimitDisplayTests.swift` |
| Reset text (rounded to the minute), money, tokens, "Updated N min ago" | `Formatters.swift` | `FormattingTests` in `PaceAndFormattingTests.swift` |
| Currency: conversion, symbols, decimals, rate line | `Currency.swift` | `CurrencyTests.swift` |
| Fetch policy, 429 presentation, persisted state | `FetchPolicy.swift` | `RateLimitTests.swift` |
| Label model (styles, metric order, stale, "–", the spark alone with no account shown), settings defaults and migrations | `MenuBarModel.swift`, `Models.swift` | `MenuBarModelTests.swift` |
| First-run accounts from what was found, one default account per source, what the dropdown shows for 0, 1 or more accounts | `AccountSetup.swift`, `Models.swift` | `AccountSetupTests.swift` |
| Cost scanner (de-duplication, cache split, incremental reads, day buckets) and pricing | `CostScanner.swift`, `Pricing.swift` | `CostScannerTests.swift` |
| claude.ai cookie flow: organization choice, Cloudflare and scope detection | `ClaudeAiCookieClient.swift` | `ClaudeAiCookieClientTests` in `SafariCookieTests.swift` |
| Claude app token search and choice, shape dump without values | `DesktopTokenParser.swift` | the token-parser tests in `DesktopCredentialTests.swift` (not the AES-CBC ones) |

macOS-only, don't port: `SafariCookieReader.swift` (and `SafariCookieParserTests`), the AES-CBC path in
`ChromiumCrypto.swift`, the Keychain calls in `Credentials.swift`, `Log.swift` (`os.Logger`).

## Paths

| Data | macOS | Windows |
|---|---|---|
| Claude Code credentials | Keychain, service `Claude Code-credentials` | `%USERPROFILE%\.claude\.credentials.json` (verified) |
| Claude Code account info | `~/.claude.json` | `%USERPROFILE%\.claude.json` (unverified) |
| Claude Code logs | `~/.claude/projects` | `%USERPROFILE%\.claude\projects` (unverified) |
| Claude app sign-in | `~/Library/Application Support/Claude/config.json` | `%APPDATA%\Claude\config.json` (unverified) |
| Usage Bar data | `~/Library/Application Support/UsageBar` | `%APPDATA%\UsageBar` (choice for the port) |

## Linux

Not planned. If someone picks it up: Claude Code keeps credentials in `~/.claude/.credentials.json`
(verified, same docs page), and Electron's `safeStorage` uses kwallet or libsecret, falling back to a
hard-coded password when neither exists (Electron docs above).
