# Data sources

This is the cross-platform contract: every Usage Bar build, on any platform, reads the same data in
the same way. The endpoints, response handling, log rules and prices below apply everywhere. The
sign-in sources describe how the macOS app (the reference implementation) finds each credential;
[PORTING.md](PORTING.md) maps them to Windows.

Every source is read-only. Usage Bar never refreshes a token, never writes a cookie, never changes
another app's files or Keychain items, and keeps tokens and cookies in memory for one request only.

## Sign-in sources

### Claude Code login

- **Reads:** the Keychain generic password with service `Claude Code-credentials`, through the
  subprocess `/usr/bin/security find-generic-password -s "Claude Code-credentials" -w`. The payload is
  JSON: `claudeAiOauth.accessToken`, `claudeAiOauth.expiresAt` (milliseconds since 1970) and other
  fields Usage Bar ignores.
- **Also reads:** `~/.claude.json`, field `oauthAccount` (email, account uuid, organization uuid) for
  display and for telling the personal organization apart from a work one.
- **Permission:** none. `/usr/bin/security` is in the item's access list, so macOS shows no prompt. A
  direct `SecItemCopyMatching` from Usage Bar would prompt, which is why it uses the subprocess.
- **Timeout:** the subprocess gets 10 seconds, then Usage Bar kills it.
- **Never:** refreshes the token. Claude Code refreshes its own token; a refresh from here could sign
  the CLI out.
- **Cost logs:** `~/.claude/projects` (only this source has them).
- **Only the default config folder** (`~/.claude`) works today. The Keychain naming for other folders
  is unconfirmed.

| State | Shown as |
|---|---|
| No Keychain item | "Not signed in to Claude Code — run `claude` once" |
| `expiresAt` passed | "Claude Code login expired — run `claude` once" (no request made) |
| Keychain read took over 10 s | "Keychain read timed out — try again" |
| Usage request 401/403 | "Claude Code login rejected — run `claude` once" |

### Claude app login

- **Reads:** `~/Library/Application Support/Claude/config.json`: `lastKnownAccountUuid`, and
  `oauth:tokenCacheV2` / `oauth:tokenCache`, which are Electron `safeStorage` strings (base64 of
  `v10` + ciphertext).
- **Key:** the Keychain generic password with service `Claude Safe Storage`, account `Claude Key`
  (falls back to service only). PBKDF2-HMAC-SHA1 with salt `saltysalt`, 1003 iterations, gives a
  16-byte key; AES-128-CBC with an IV of 16 spaces decrypts. Usage Bar checks PKCS7 padding itself, so
  a wrong key fails instead of producing garbage.
- **Choosing the token:** the parser walks the decrypted JSON for objects with an access token and an
  expiry. It prefers one tied to `lastKnownAccountUuid`, then a cached choice (only while unexpired),
  then probes candidates against `/api/oauth/profile` and requires an account uuid match. It accepts a
  "not the personal organization" match only when it knows the personal organization.
- **Fallback:** if the token route fails with 401/403, it reads the `sessionKey` cookie for claude.ai
  from the app's Chromium `Cookies` database (read-only, `immutable=1`, never copied to disk), strips
  the SHA-256 host prefix newer Chromium adds, and uses the claude.ai cookie flow below.
- **Permission:** Keychain access to "Claude Safe Storage". Usage Bar never asks until you click
  **Connect…**; macOS then prompts once ("Always Allow" keeps it).

| State | Shown as |
|---|---|
| Not connected yet | "Allow Usage Bar to read Claude Safe Storage" + Connect… |
| Keychain prompt denied | Same message; Connect… asks again |
| The app is signed in to the CLI's personal account | "The Claude app is signed in to your Personal account" (no numbers) |
| Token expired, or 401/403 and no cookie | "Open the Claude app to refresh its sign-in" |
| 403 for scope | "This sign-in can't read usage (scope)" |

### Safari session

- **Reads:** `*.binarycookies` files, keeping only cookies for `claude.ai` and `*.claude.ai`
  (`sessionKey`, `lastActiveOrg`). It parses each file in memory and never writes to it.
- **Where it looks (an honest note):** the two well-known Safari files
  (`~/Library/Containers/com.apple.Safari/Data/Library/Cookies/Cookies.binarycookies` and the legacy
  `~/Library/Cookies/Cookies.binarycookies`), every `*.binarycookies` up to six levels down in the
  Safari container, and every `*.binarycookies` under `~/Library/Cookies` and `~/Library/HTTPStorages`.
  The last two folders hold other apps' cookie stores too. A narrower search that read only Safari's
  container found no claude.ai session on the development Mac, while the full sweep did, so the sweep
  stays until someone confirms where Safari keeps the live session on macOS 26 and 27.
- **Permission:** Full Disk Access. macOS applies a new grant only after Usage Bar relaunches.
- **Format:** `cook` magic, big-endian page count and page sizes, little-endian cookie records with
  domain/name/path/value offsets and Mac-epoch (2001-01-01) expiry. Layout per
  [libyal dtformats](https://github.com/libyal/dtformats/blob/main/documentation/Safari%20Cookies.asciidoc).

| State | Shown as |
|---|---|
| Container unreadable (no Full Disk Access) | "Give Usage Bar Full Disk Access" + Open Privacy Settings… |
| No `sessionKey` found | "No claude.ai sign-in found in Safari" |
| Cookie rejected (401/403) | "Safari sign-in rejected — open claude.ai in Safari" |
| Cloudflare challenge | "Blocked by a Cloudflare check — open claude.ai in Safari" |
| Only the personal organization | "Safari is signed in to your Personal account" (no numbers) |

### Failures any source can show

| State | Shown as |
|---|---|
| 429 with numbers on screen | Numbers stay; header shows "Paused by rate limit · retrying 6:40 am" |
| 429 with no numbers yet | "Rate limited — retrying after 6:40 am" |
| Network error | "Network error: …" with the last numbers greyed |
| Other HTTP error | "Usage request failed (HTTP n)" |

## Endpoints

### `GET https://api.anthropic.com/api/oauth/usage`

Headers: `Authorization: Bearer <access token>`, `anthropic-beta: oauth-2025-04-20`,
`Accept: application/json`, `User-Agent: claude-cli/<version> (external, cli)` (the version comes
from the installed Claude Code, falling back to a built-in default).

Response (values made up; unknown keys and nulls are ignored):

```json
{
  "five_hour": {"utilization": 33.0, "resets_at": "2026-03-02T08:20:00.393827+00:00"},
  "seven_day": {"utilization": 77.0, "resets_at": "2026-03-04T15:00:00.393846+00:00"},
  "extra_usage": {"is_enabled": false, "monthly_limit": 10000, "used_credits": 1250.0,
                  "utilization": 12.5, "currency": "AUD", "decimal_places": 2,
                  "disabled_reason": "out_of_credits"},
  "limits": [
    {"kind": "session", "group": "session", "percent": 33, "severity": "normal",
     "resets_at": "…", "scope": null, "is_active": false},
    {"kind": "weekly_all", "group": "weekly", "percent": 77, "severity": "critical",
     "resets_at": "…", "scope": null, "is_active": true},
    {"kind": "weekly_scoped", "group": "weekly", "percent": 5, "severity": "normal",
     "resets_at": "…", "scope": {"model": {"id": null, "display_name": "Fable"}}, "is_active": false}
  ]
}
```

- `limits[]` wins; `five_hour` / `seven_day` are the fallback.
- Windows: session 300 minutes, weekly 10080 minutes.
- `resets_at` has fractional seconds and an offset. A reset can arrive a fraction of a second early
  (`14:59:59.6Z` for a 15:00 reset), so day-and-time resets round to the nearest minute. A null
  `resets_at` means the window hasn't started ("starts on first use").
- Extra usage money is in minor units with `decimal_places`.
- 401 means the token is bad, and 403 with a scope message means the token lacks the scope.
  429 means wait (`Retry-After`, seconds or an HTTP date).

### `GET https://api.anthropic.com/api/oauth/profile`

Same headers. Usage Bar reads `account.email`, `account.uuid`, `organization.uuid`,
`organization.name` and `organization.organization_type`.

### `GET https://claude.ai/api/organizations` and `GET https://claude.ai/api/organizations/{uuid}/usage`

Headers: `Cookie: sessionKey=…` (plus `lastActiveOrg` when present), `Accept: application/json`,
`Referer: https://claude.ai/` and a Safari User-Agent. The organization list is an array of objects
with `uuid` and `name`. Usage Bar picks the `lastActiveOrg` organization, else the first one that
isn't the personal organization, else the first. The usage response has the same shape as above. A
`cf-mitigated: challenge` header, or a 403/503/429 page saying "Just a moment", means Cloudflare
stepped in.

### `GET https://open.er-api.com/v6/latest/USD`

Free, no key. Usage Bar asks for it only when costs show in a currency other than US dollars: once a
day, and straight away when you pick a currency it has no rate for. It keeps every rate in the
response (`rates`) and `time_last_update_unix`, and sends nothing about you. Until the first fetch it
has a built-in AUD rate (1.423) and shows other currencies in US dollars.

## Claude Code logs (cost estimate)

Claude Code writes one JSON object per line to `<config dir>/projects/**/*.jsonl` (`~/.claude/projects`
by default), including subagent logs in subfolders. Usage Bar reads them read-only and keeps no
message content.

- **Lines it uses:** `type == "assistant"` with `message.usage`. Before decoding JSON it skips any
  line that doesn't contain both `"usage"` and `"assistant"`. Lines without a parseable `timestamp`
  are skipped.
- **Fields:** `message.id`, `requestId` (or `request_id`), `sessionId`, `timestamp`, `message.model`,
  `message.stop_reason`, and in `message.usage`: `input_tokens`, `output_tokens`,
  `cache_read_input_tokens`, `cache_creation_input_tokens`, and `cache_creation` with
  `ephemeral_5m_input_tokens` / `ephemeral_1h_input_tokens`.
- **Cache writes:** use the `cache_creation` 5-minute/1-hour split when either field is present;
  otherwise count `cache_creation_input_tokens` as 5-minute writes.
- **De-duplication key:** `message.id:requestId`; without a request id, `sessionId:message.id`;
  without a session, `message.id`. Claude Code writes several lines per message while streaming and
  copies history into resumed sessions, so the same key appears more than once, across files too.
- **Which line wins:** an "incomplete" line (no `stop_reason`, input > 0, output 0, no cache fields)
  never replaces a complete one, and a complete line always replaces an incomplete one. Otherwise the
  larger token total wins and ties go to the later line. Lines with a zero total are ignored.
- **Days:** each record goes into the local calendar day of its `timestamp`, then by model. Files
  untouched for 31 days are skipped.

The test cases in `macos/Tests/UsageBarTests/CostScannerTests.swift` pin each rule down.

## Prices

US dollars per million tokens. Source: the Anthropic pricing page,
<https://platform.claude.com/docs/en/about-claude/pricing>, read on 2026-10-25.

| Model | Input | Output | Cache write 5 min | Cache write 1 h | Cache read |
|---|---|---|---|---|---|
| `claude-opus-5-5` | 4.00 | 20.00 | 5.00 | 8.00 | 0.20 |
| `claude-sonnet-5-5` | 2.00 | 10.00 | 2.50 | 4.00 | 0.10 |
| `claude-haiku-5-5` | 0.10 | 0.50 | 0.125 | 0.20 | 0.01 |
| `claude-fable-5-1` | 10.00 | 50.00 | 12.50 | 20.00 | 0.25 |

- Haiku 5.5's higher price for prompts over 100K tokens is not applied.
- Other models map by family name (opus, sonnet, haiku, fable) to the row above. Older models with
  different prices (for example `claude-opus-5` at $5/$25) are therefore under- or over-estimated.
- Models with no family match cost zero and appear in the dropdown footnote as unpriced.
- Fast mode and US-only inference multipliers are not applied.
