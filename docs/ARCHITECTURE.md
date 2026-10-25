# Architecture

Usage Bar is one SwiftPM package with three targets:

| Target | Kind | Holds |
|---|---|---|
| `UsageBarCore` | library | Everything testable: models, decoding, pace maths, formatting, pricing, the cost scanner, credential readers, fetch policy. No AppKit or SwiftUI. |
| `UsageBar` | executable | The app shell: status item, dropdown, Settings window, demo data, debug commands. |
| `UsageBarTests` | tests | XCTest suite against `UsageBarCore`, synthetic data only. |

`scripts/build.sh` wraps the executable into `Usage Bar.app`, adds the glyph and icon resources,
writes `Info.plist` and signs it.

## Core modules (`Sources/UsageBarCore`)

| File | Role |
|---|---|
| `Models.swift` | `Account`, `AccountSource` (Claude Code / Claude app / Safari), `AppSettings` with its one-time migrations, `Paths`. |
| `Usage.swift` | `UsageDecoder` (lenient), `UsageLimit`, `UsageSnapshot`, `ExtraUsage`, `Profile`, ISO-8601 parsing. |
| `UsageClient.swift` | OAuth usage/profile requests, 429 Retry-After parsing, scope-error detection, exchange rate. |
| `ClaudeAiCookieClient.swift` | claude.ai organizations + usage with a session cookie; org choice; Cloudflare detection. |
| `AccountFetcher.swift` | One fetch per account for any source; `AccountIssue` (every failure state and its message). |
| `Credentials.swift` | Claude Code login via `/usr/bin/security`; Claude app config, Safe Storage key and cookie store. |
| `ChromiumCrypto.swift` | Chromium "v10" decryption (PBKDF2 + AES-128-CBC) with strict PKCS7 checks. |
| `DesktopTokenParser.swift` | Finds access tokens in the Claude app's decrypted token cache; `--dump-desktop-shape`. |
| `SafariCookieReader.swift` | `Cookies.binarycookies` parser and the Safari file sweep. |
| `FetchPolicy.swift` | When an account may fetch; how a 429 is presented; the persisted per-account state. |
| `Pace.swift`, `LimitDisplay.swift` | Pace maths and the text for each limit row. |
| `Formatters.swift` | Reset times, money, token counts, clock times, the currency line. |
| `MenuBarModel.swift` | What the menu bar label shows, as data (rendering lives in the app). |
| `Pricing.swift` | API prices per model and the cost calculation. |
| `CostScanner.swift` | Incremental scanner over Claude Code's JSONL logs. |
| `GlyphTrim.swift` | Trims transparent padding from the menu bar glyph. |
| `Log.swift` | `os.Logger` categories: fetch, cost, ui, auth. |

## App modules (`Sources/UsageBar`)

| File | Role |
|---|---|
| `main.swift` | Runs a debug command if one was given, else starts the app. |
| `StatusController.swift` | `NSStatusItem`, the `NSMenu` (a SwiftUI view plus native items), debug notification hooks, `AppDelegate`. |
| `AppStore.swift` | The `@MainActor` observable store: settings, per-account state, polling, persistence, costs, rate. |
| `DropdownViews.swift` | Dropdown layouts A (both accounts) and B (one at a time). |
| `SettingsView.swift` | Toolbar-style Settings window (`NSTabViewController`) with General, Accounts and About tabs. |
| `MenuBarRenderer.swift` | Draws the label as a template image; the Claude glyph. |
| `CostService.swift` | Runs the cost scanner off the main thread and keeps its cache file. |
| `DemoData.swift` | Sample data for demo mode. |
| `DebugCommands.swift`, `DebugSnapshot.swift` | Command-line debug entry points and the menu bar sample sheet. |

## Data flow

1. `AppDelegate` creates the `AppStore` (demo or normal) and the `StatusController`, then calls
   `store.start()`.
2. The store asks `AccountFetcher` to fetch each account. The fetcher reads the account's credential
   (read-only), calls the usage endpoint, and returns a `FetchReport`: a `UsageSnapshot` or an
   `AccountIssue`, plus the HTTP status and display details.
3. The store keeps one `AccountState` per account and saves it to
   `~/Library/Application Support/UsageBar/state.json`.
4. `StatusController` listens to the store. On any change it rebuilds the `MenuBarModel`, redraws the
   label, and resizes the dropdown's hosting view. SwiftUI views read the store directly.

## Polling and backoff

- A `.common`-mode timer fetches every refresh interval (1, 2, 5 or 10 minutes; default 2), so it
  keeps firing while the menu is open.
- `FetchPolicy.shouldFetch` decides every fetch:
  - Nothing fetches inside an account's Retry-After window.
  - A launch, or opening the menu, fetches an account only when its last attempt is at least one
    refresh interval old. The last attempt time is saved to disk, so relaunching costs no requests.
  - The timer and Refresh Now always fetch, outside a Retry-After window.
- On HTTP 429 the account waits for Retry-After (seconds or an HTTP date), or 5 minutes without one.
  `AccountPresentation` keeps the last good numbers on screen and puts "Paused by rate limit ·
  retrying 6:40 am" in the account header. With no data yet, it shows the error instead.
- Every 429 goes to the log (category `fetch`) with the account tag and the retry time.

## Cost scanner

`CostScanner` reads `~/.claude/projects/**/*.jsonl`, including subagent logs in subfolders.

- **Incremental.** For each file it keeps size, mtime, inode and the byte offset after the last
  complete line. It reads only new bytes. A file that shrank or changed inode is read again from
  the start. Files untouched for 31 days are skipped.
- **Pre-filter.** A line has to contain `"usage"` and `"assistant"` before any JSON decoding.
- **De-duplication.** The key is `message.id:requestId` (also `request_id`); without a request id it
  is `sessionId:message.id`. Claude Code writes several lines per message while streaming, and the
  later ones carry the full usage, so the scanner keeps the line with the largest token total (ties go
  to the later line). An "incomplete" line (no `stop_reason`, input > 0, output = 0, no cache fields)
  never replaces a complete one.
- **Cache writes.** It uses the 5-minute/1-hour split when `cache_creation` has it, and counts the
  aggregate `cache_creation_input_tokens` as 5-minute writes only when the split is missing.
- **Aggregation and pricing.** Records go into buckets per local day and model. `Pricing` turns them
  into US dollars; the store converts to AUD with the daily rate.
- **Cache file.** `cost-cache.json` holds file positions and packed records: a 64-bit hash of the key,
  day, model index and five token counts. It holds no message content and no ids.
- The scan runs on a background queue with up to six files in parallel, at launch and every
  5 minutes. The dropdown shows "Counting…" until the first scan finishes.

## Menu bar rendering

`MenuBarModel.build` turns each shown account's snapshot into values (session, weekly, model weekly;
whichever are on). `MenuBarRenderer` draws the Claude glyph (the Claude app's tray icon template,
trimmed and drawn 16 pt tall), then per account an outlined tag and either text or stacked bars.

The image is always a template image, so macOS tints it for light and dark menu bars. Data older than
15 minutes draws at 50% opacity; an account with no data shows its tag and "–".

## Settings storage

`AppSettings` is saved as JSON in the preferences under `settings.v1`. Decoding treats every field as
optional, so older saved settings keep working. `applyMigrations()` runs once on settings saved before
the current defaults: it switches the style to Bars and moves a never-connected Claude app account to
the Safari source. The exchange rate and account emails have their own keys. No secret ever goes into
preferences.

## Demo mode

`--demo` or `USAGEBAR_DEMO=1` starts the store with built-in sample data. It makes no network
requests, reads no credentials or logs, and saves nothing. The README screenshots come from it.
