# Decisions

Short records of choices that shape the project. Newest last.

## 1. Independent of CodexBar

**Context.** CodexBar shows Codex and Claude limits and inspired this app.
**Decision.** Usage Bar shares no code, data or files with CodexBar and depends on nothing from it. It
gets usage data only from Anthropic (the API and claude.ai).
**Consequence.** Every reader, decoder and endpoint here was written and verified for this project.

## 2. Read-only access to other apps' sign-ins

**Context.** Claude Code and the Claude app own their tokens and refresh them on their own schedule.
**Decision.** Usage Bar reads credentials and never refreshes, writes or deletes them. It has no
sign-in flow.
**Consequence.** An expired sign-in shows which app to open. Refreshing a CLI token from here could
sign the CLI out, so that option stays off the table.

## 3. Prices come from the live pricing page

**Context.** The bundled Claude API reference and the live pricing page disagreed on one figure
(Sonnet 5.5 cache reads: $0.20 against $0.10). The page's footnote says Opus 5.5 and Sonnet 5.5 cache
hits cost 0.05× input.
**Decision.** `Pricing.swift` follows the live page and names its source and date.
**Consequence.** Re-check the page when prices change and update the table and its date.

## 4. A monochrome menu bar

**Decision.** The menu bar label never changes colour with usage: no orange values, no red pills.
**Consequence.** The label is always a template image. Warn and critical thresholds were removed.

## 5. Bars as the default style

**Decision.** New installs, and settings saved before this default, use the Bars style (two stacked
bars per account). Percentages, Compact and Icon only stay available.

## 6. Dropdown A as the default

**Decision.** The dropdown shows all accounts at once by default. Layout B (tabs, one account, with the
30-day chart) is a setting.

## 7. De-duplicating Claude Code log records

**Context.** Claude Code writes several lines per message while streaming. Later lines carry the
complete usage, and resumed sessions copy history into new files.
**Decision.** The key is `message.id:requestId` (fallback `sessionId:message.id`). Keep the line with
the largest token total; ties go to the later line; an incomplete line never replaces a complete one.
Use the 5-minute/1-hour cache-write split when present and count the aggregate only without it.
**Consequence.** Keeping the first line would undercount; summing every line would overcount.

## 8. The app icon is an Icon Composer document

**Context.** macOS 26 puts icons that don't fill its shape into a grey frame. The first icon had
transparent margins and edge pixels just under full opacity.
**Decision.** `design/icon/AppIcon.icon` holds full-canvas layers (slate gradient fill, orange spark,
orange and blue bars). `macos/scripts/build.sh` compiles it with `actool` into `Assets.car` plus an
`.icns`, and sets `CFBundleIconName`. `design/icon/make_icon.py` redraws the layers.
**Consequence.** The system applies the shape and lighting. Icon Composer's file format has no
published schema; the keys follow a working third-party tool and `ictool` renders the result.

## 9. The Safari source sweeps cookie folders

**Context.** The source reads a claude.ai session cookie from `*.binarycookies` files. A search
limited to Safari's container found no session on the development Mac; the broader search found it.
**Decision.** Search Safari's files and container, plus every `*.binarycookies` under
`~/Library/Cookies` and `~/Library/HTTPStorages`, keeping only claude.ai cookies.
**Consequence.** The sweep reads other apps' cookie files (in memory, filtered). Narrow it once someone
confirms where Safari keeps the live session.

## 10. Spend rate-limit budget carefully

**Context.** Relaunches, menu opens and debug runs all hit the usage endpoint and caused 429s.
**Decision.** A launch or menu open fetches only when the last attempt is a full refresh interval old
(saved to disk). A 429 keeps the last numbers and notes the pause in the header. Nothing fetches inside
a Retry-After window.

## 11. One stable signing identity

**Decision.** Sign every build with the same identity (`USAGEBAR_SIGN_IDENTITY`) and keep the bundle id.
**Consequence.** macOS keeps Keychain "Always Allow", Full Disk Access and Login Items grants across
rebuilds. Ad-hoc signing works but resets them each time.

## 12. The Claude app source waits for Connect

**Decision.** Usage Bar doesn't touch "Claude Safe Storage" until you click Connect…, so installing or
launching the app never pops a Keychain prompt unasked.

## 13. The menu bar spark is a vector trace

**Context.** Earlier builds shipped the Claude app's tray icon PNGs in the repository.
**Decision.** `design/spark/trace_spark.py` traced the Claude app's `TrayIconTemplate@3x.png` once into
a vector (marching squares, smoothing, Catmull-Rom Beziers) and wrote it to
`macos/Sources/UsageBar/ClaudeSparkPath.swift` and `design/spark/ClaudeSpark.svg`. The app draws that
path; it ships no PNG and reads nothing from the Claude app at runtime. Other platforms draw the same
shape from the SVG.
**Consequence.** The spark is crisp at every scale and matches the original at 16 pt: compared side by
side on a 2× menu bar, and in 1×, 2× and 3× renders, where the differences sit on anti-aliased edges.
The shape is still the Claude spark; Usage Bar uses it to say "Claude usage" and is not made or
endorsed by Anthropic.

## 14. MIT licence and an authorship note

**Decision.** The project is MIT licensed so friends can use and change it. The README and the About
tab say it was built by AI (Claude, by Anthropic) under the direction of its author, Sipheren.
`scripts/check-secrets.sh` allows exactly the author's published address and nothing else.

## 15. Costs in a chosen currency

**Decision.** Costs are computed in US dollars and shown in the currency picked in Settings (AUD by
default, so existing installs look the same). Choosing USD skips the exchange-rate request entirely.
Extra-usage amounts stay in the currency the API reports, because that's what the plan bills.

## 16. One account per sign-in source; first run adds only what's signed in

**Context.** Every fresh install started with Personal (Claude Code) and Work (Safari), so a Mac without
a Safari sign-in showed an empty Work account forever.
**Decision.** A build shows one or more accounts, up to one per sign-in source (Claude Code login,
Claude app login, browser session). The first launch adds only the sign-ins it finds, with no prompt
and no secret read; Safari is never probed then. Nothing found means no accounts, and the dropdown and
Settings say so. The dropdown, menu bar and Settings work at any count. Several accounts on one source
(more Claude Code config folders, several browser sessions) are a separate change.
**Consequence.** Saved settings load exactly as they are; no migration adds, removes or renames
accounts. The detection rules are in [DATA-SOURCES.md](DATA-SOURCES.md#first-run-detection).
