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
Decision 18 changes how often each sign-in polls and how a 429 is waited out.

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

**Decision.** The project is MIT licensed so friends can use and change it. The About tab's footer
reads "Made with Claude by Sipheren", and the full note is one line at the end of the README; nowhere
else, and never at the top of a page.
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
accounts, and an account a build can't read is kept and saved back unchanged. The sign-in source picker
only offers sources no other account uses. The detection rules are in [DATA-SOURCES.md](DATA-SOURCES.md#first-run-detection).

## 17. Account colours in the Windows tray

**Context.** A Windows tray icon is one small square (20 px at 125% scaling). The Mac label's tag letters
don't fit beside the bars, and a tag badge over the bars wasn't readable at that size.
**Decision.** On Windows, each account's tray icon shows only its bars, or its session and weekly
percentages as numbers, drawn in that account's colour. The colour tells the accounts apart, as other tray
apps do. Usage still never changes a colour: decision 4's rule for usage stands, and the macOS menu bar stays
monochrome. The maintainer's call, 2026-10-26.
**Consequence.** Account colours have to read on both a dark and a light taskbar, so they are ten presets
rather than a free choice: each has a shade for dark surfaces, one for light ones and a badge shade, used
the same in the tray and the flyout. Tests hold every preset to measured contrast (at least 6.8:1 on the
dark taskbar, 4.5:1 elsewhere) and keep any two clearly apart. The Icon only spark stays monochrome.

## 18. Poll the OAuth usage endpoint sparingly (changes 10)

**Context.** Relaunches, menu opens and debug runs all hit the usage endpoint and caused 429s. The OAuth
usage endpoint (Claude Code, and the Claude app's token) allows only about 5 requests every few minutes,
shared with Claude Code's own `/usage`, so a 2-minute poll kept hitting 429. Anthropic often answers
with `Retry-After: 0`, which the app took as "no wait". claude.ai's usage endpoint (the Safari and
Claude app cookies) has its own allowance and is fine at 2 minutes.
**Decision.** Nothing fetches inside a Retry-After window. Retry-After is honoured up to an hour; when
it is missing or 0 the account waits 5 minutes. A sign-in on the OAuth endpoint polls no faster than
every 5 minutes, whatever the setting; cookie sign-ins keep the setting. The Claude app tries its
cookie first and uses its token only when the cookie doesn't work. A 60-second timer fetches each
account when its own interval is due (less 30 seconds of slack for the tick). Opening the menu fetches
when the last attempt is 2 minutes old, or the interval if shorter. A launch fetches only when the last
attempt is a full interval old (saved to disk). A 429 keeps the last numbers and notes the pause in the
header.
The maintainer's call, 2026-10-27.
**Consequence.** In the background, Claude Code numbers can be about 5 minutes old; opening the menu
refreshes them once they are 2 minutes old. Which route the Claude app last used isn't saved, so after a
relaunch it counts as a cookie sign-in until its first fetch.
Decision 19 changes the token sign-ins' numbers, the menu rule for them, and saves the route.

## 19. Back off from the OAuth usage endpoint (changes 18)

**Context.** 0.3.1 still hit 429 on a Claude Code login. On 2026-10-28 one account got a 429 with
`Retry-After: 3471`; the app retried on the boundary and got `Retry-After: 3600`; one later read
worked, and the next, 5 minutes on, got `Retry-After: 3402`. The endpoint is shared with Claude Code's
own `/usage` and with every other app and machine polling the same account, so honouring Retry-After
alone doesn't keep an account clear. Anthropic publishes no limits for `GET /api/oauth/usage`; the
"about 5 requests every few minutes" in decision 18 was this project's guess, not a published figure.
Others report Retry-After values of 0 (while still blocked), 471, 500 to 700, 3037 and 3600 seconds,
blocks of up to an hour, the endpoint "saturated for hours" with many pollers, and retries inside the
window keeping the block alive. Whether the limit is per account or per token is unknown. Reports:
[anthropics/claude-code#30930](https://github.com/anthropics/claude-code/issues/30930),
[#31021](https://github.com/anthropics/claude-code/issues/31021),
[#31637](https://github.com/anthropics/claude-code/issues/31637),
[#77477](https://github.com/anthropics/claude-code/issues/77477) and
[stablyai/orca#9616](https://github.com/stablyai/orca/issues/9616).
**Decision.** A sign-in on the OAuth endpoint (Claude Code, and the Claude app on its token) polls no
faster than every 15 minutes, never fetches when the menu opens, and fetches on Refresh Now only outside
a wait and once its last attempt is 5 minutes old. Any 429, on either endpoint, waits Retry-After
(seconds or an HTTP date; 5 minutes when it is missing, 0 or negative; at most an hour) plus a random 2
to 10 minutes, so the retry never lands on the boundary. Each 429 also doubles that account's background
interval, up to an hour (15, 30, 60 minutes on the token; the setting doubling on a cookie), and 3 good
reads in a row step it back one level. The wait, the backoff level and the route the last fetch used
are saved with the other fetch state. A wait saved by 0.3.1 gets the jitter on the first launch and
counts as one 429. Cookie sign-ins keep the setting and decision 18's menu rule.
The maintainer's call, 2026-10-28.
**Consequence.** Claude Code numbers can be 15 minutes old, and up to an hour after repeated 429s, so
each account in the dropdown says how old its numbers are. Numbers older than 15 minutes still draw at
half opacity, so a backed-off account greys out between checks. A 429 costs at least its Retry-After
plus 2 minutes.
