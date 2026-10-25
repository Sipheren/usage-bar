# Porting to Windows and Linux

No port exists yet. This page separates what carries over from what is macOS-only.

## Portable

These rules and formats don't depend on macOS. Reuse them as they are, in Swift or another language.

| Piece | Where | Notes |
|---|---|---|
| Usage endpoint contract | `docs/DATA-SOURCES.md`, `Usage.swift` | Same HTTP requests and lenient decoding on any OS. |
| claude.ai cookie flow | `ClaudeAiCookieClient.swift` | Organization choice, Cloudflare detection, usage decoding. |
| Pace maths and limit text | `Pace.swift`, `LimitDisplay.swift`, `Formatters.swift` | Pure functions with tests. |
| Fetch policy and 429 handling | `FetchPolicy.swift` | Pure functions with tests. |
| Pricing and cost maths | `Pricing.swift` | One table and one function. |
| Cost scanner rules | `CostScanner.swift` | Claude Code writes the same JSONL on every OS. Only the logs folder differs. |
| Menu bar text model | `MenuBarModel.swift` | Data only; each platform draws it its own way. |
| Chromium `v10` decryption | `ChromiumCrypto.swift` | The AES scheme is macOS-specific (see below), but the code is plain crypto. |

`UsageBarCore` uses Foundation everywhere. A few files import Apple-only modules and need
replacements: `Credentials.swift` (Security, SQLite3), `ChromiumCrypto.swift` (CommonCrypto,
CryptoKit), `GlyphTrim.swift` (CoreGraphics), `Log.swift` (os).

## macOS-only

| Piece | macOS mechanism | Windows / Linux equivalent to find |
|---|---|---|
| Claude Code login | Keychain item via `/usr/bin/security` | Where Claude Code stores credentials on each OS (a credentials file is likely; confirm before relying on it). |
| Claude app sign-in key | Keychain "Claude Safe Storage", PBKDF2 + AES-128-CBC | Electron `safeStorage`: DPAPI with AES-GCM on Windows; libsecret or KWallet on Linux. |
| Safari cookies | `*.binarycookies` | None. A browser source on Windows/Linux would read Chrome/Edge/Firefox stores, each with its own encryption. |
| Menu bar item | `NSStatusItem`, `NSMenu`, SwiftUI | Windows notification-area icon; Linux StatusNotifierItem (AppIndicator). |
| Settings window | `NSTabViewController`, SwiftUI `Form` | Native settings UI per platform. |
| Launch at login | `SMAppService` | Registry Run key or Task Scheduler; XDG autostart `.desktop` file. |
| Logging | `os.Logger` | Event log / ETW; journald or stderr. |
| App icon | Icon Composer + `actool` | `.ico`; PNG set plus a `.desktop` file. |
| Packaging and signing | `build.sh`, `codesign` | MSIX or an installer with Authenticode; AppImage, Flatpak or distro packages. |

## Paths to change

| Data | macOS | Windows | Linux |
|---|---|---|---|
| Claude Code logs | `~/.claude/projects` | `%USERPROFILE%\.claude\projects` | `~/.claude/projects` |
| Claude Code account info | `~/.claude.json` | `%USERPROFILE%\.claude.json` | `~/.claude.json` |
| App data | `~/Library/Application Support/UsageBar` | `%APPDATA%\UsageBar` | `$XDG_DATA_HOME/usagebar` |

Confirm each Windows and Linux path on a real machine before you rely on it.
