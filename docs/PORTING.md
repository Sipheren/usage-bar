# Porting: the Windows starting brief

The Windows port lives in `windows/` (0.1.0, C# on .NET 10 with WPF; see
[windows/README.md](../windows/README.md)). The macOS app in `macos/` is the reference implementation:
when this page and the macOS code disagree, the code is right and this page needs fixing.

Read these first: [AGENTS.md](../AGENTS.md) (rules), [DATA-SOURCES.md](DATA-SOURCES.md) (the data
contract), [SPEC.md](SPEC.md) (behaviour), [DECISIONS.md](DECISIONS.md) (choices you keep).

Status markers below: **verified** means checked against the cited source, or (marked "on Windows 11")
confirmed by the Windows build on a real machine on 2026-10-26; **unverified** means nobody has confirmed
it on a Windows machine yet. Confirm every unverified item before relying on it, then update this page.

## Data sources on Windows

### Claude Code login

- **Credentials:** `%USERPROFILE%\.claude\.credentials.json`, or `.credentials.json` in
  `%CLAUDE_CONFIG_DIR%` when that variable is set. The file inherits the user profile's access
  controls. **Verified** against the Claude Code docs, "Credential management":
  <https://code.claude.com/docs/en/iam>.
- **JSON shape: verified on Windows 11.** The same payload macOS keeps in the Keychain:
  `claudeAiOauth.accessToken` and `claudeAiOauth.expiresAt` (milliseconds since 1970), plus
  `refreshToken`, `scopes`, `subscriptionType` and `rateLimitTier`, which Usage Bar ignores.
- **Account info:** `%USERPROFILE%\.claude.json`, field `oauthAccount`, with `emailAddress` (not `email`),
  `accountUuid`, `organizationUuid` and `organizationName`. **Verified on Windows 11.**
- **Logs for the cost estimate:** `%USERPROFILE%\.claude\projects\**\*.jsonl`, subagent logs in subfolders,
  as on macOS. **Verified on Windows 11.**
- **CLI version for the User-Agent:** the newest folder name in `%USERPROFILE%\.local\share\claude\versions`
  (`claude.exe` in `.local\bin` is the binary, not a link). **Verified on Windows 11.**
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
- **Verified on Windows 11:**
  - the Claude app encrypts `oauth:tokenCacheV2` and `oauth:tokenCache` this way: `v10`, AES-256-GCM, with
    the DPAPI-wrapped key from `Local State` (decrypted in memory; only key names and types were inspected);
  - `config.json` and `Local State` live in `%APPDATA%\Claude\`;
  - the cookie database is `%APPDATA%\Claude\Network\Cookies`, with `sessionKey` and `lastActiveOrg` for
    claude.ai encrypted as `v10`;
  - the cache's keys look like `acct:<account uuid>|<uuid>:<organization uuid>:https://api.anthropic.com:<scopes>`,
    each holding `token`, `refreshToken` and `expiresAt` (milliseconds), so the parser matches the account
    uuid in the key, as on macOS.
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
- **Firefox:** `cookies.sqlite` in the profile folder, with values not encrypted. **Verified on
  Windows 11** (a claude.ai `sessionKey` read in plain text, Firefox not running); not built yet. Firefox holds the database open while it runs; open it with SQLite's `immutable=1`
URI flag, as the macOS reader does for the Claude app's cookies, and never copy it to disk.
- Practical order: build the Claude Code source first, then the Claude app source. Leave a browser
  source until the other two work.

### First run

Follow [DATA-SOURCES.md](DATA-SOURCES.md#first-run-detection): one or more accounts, up to one per
sign-in source, and a first run adds only the sign-ins it finds, with no prompt and nothing decrypted.
Zero accounts is a normal state. On Windows:

- Claude Code: the `.credentials.json` above exists. Don't parse the token to decide.
- Claude app: its `config.json` has a non-empty `oauth:tokenCacheV2` and a `lastKnownAccountUuid` that
  isn't `oauthAccount.accountUuid` in `%USERPROFILE%\.claude.json`. Don't call DPAPI for this check.
- Browser session: never checked on first run; the user adds it from Settings.

Verified on Windows 11 (2026-10-26) with `UsageBar.exe --detect-sign-ins` and a real first run: the
credentials file and `.claude.json` were found; the Claude app's `config.json` held a V2 token cache and a
`lastKnownAccountUuid` equal to the Claude Code account's, so the first run added Personal only. A Claude
app signed in to a different account is covered by tests only. Save the first run's settings straight
away: Windows 0.1.0 to 0.1.3 didn't, so every launch gave the account a new id and a new tray-icon entry.

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
- **Colour:** each account's icon is drawn in that account's colour (decision 17), one of ten presets with a
  shade for dark and one for light taskbars; the tray and the flyout use the same shade. The Icon only spark
  stays monochrome, white on a dark taskbar and black on a light one. A tinted bar track made a clay
  orange read as red at 20 px, so the track is neutral grey. The usual signal is `SystemUsesLightTheme` under
  `HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize` (not a documented API; white on a
  dark taskbar verified on Windows 11, a light taskbar not yet seen live); redraw on `WM_SETTINGCHANGE`
  with `ImmersiveColorSet`.
- **Hidden by default: verified on Windows 11.** New tray icons go to the overflow; Windows keeps each
  icon's place under `HKCU\Control Panel\NotifyIconSettings`, keyed by the executable and the icon id, so
  ids must stay stable (the Windows build derives them from each account's id). `windows/README.md` says so.
- **Icon size: verified on Windows 11.** `GetSystemMetricsForDpi(SM_CXSMICON)` gives 20 px at 125% scaling.
  A tag letter beside or over the bars isn't readable at that size, so the icon holds only the bars, or the
  session and weekly percentages as two stacked numbers, in the account's colour.
- **One icon per account** is the default (the maintainer's choice, 2026-10-26), a single icon an option.
- **Only real accounts:** a fresh install has one account (the Claude Code login); others are added in
  Settings. An account with no sign-in behind it never appears.
- **Dropdown:** a borderless flyout on left-click, anchored to the icon (`Shell_NotifyIconGetRect`),
  closing when it loses focus. It holds Dropdown A by default, Layout B as a setting. Right-click
  opens a menu: Refresh Now, Settings…, About Usage Bar, Quit.
- **Acrylic: verified on Windows 11.** `DWMWA_SYSTEMBACKDROP_TYPE` on a WPF window with `WindowChrome`
  and a frame extended into the client area gives the flyout Windows 11's acrylic, rounded corners and
  shadow. Two traps: remove `WS_SYSMENU` or DWM draws a close button over the content, and translucent
  black (and dark anti-aliased text) disappears over the backdrop, so the light theme needs a solid
  surface.
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
| Claude Code account info | `~/.claude.json` | `%USERPROFILE%\.claude.json` (verified) |
| Claude Code logs | `~/.claude/projects` | `%USERPROFILE%\.claude\projects` (verified) |
| Claude app sign-in | `~/Library/Application Support/Claude/config.json` | `%APPDATA%\Claude\config.json` (verified) |
| Usage Bar data | `~/Library/Application Support/UsageBar` | `%APPDATA%\UsageBar`, cache and logs in `%LOCALAPPDATA%\UsageBar` |

## Linux

Not planned. If someone picks it up: Claude Code keeps credentials in `~/.claude/.credentials.json`
(verified, same docs page), and Electron's `safeStorage` uses kwallet or libsecret, falling back to a
hard-coded password when neither exists (Electron docs above).
