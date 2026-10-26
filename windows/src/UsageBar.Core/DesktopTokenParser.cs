using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace UsageBar.Core;

/// <summary>An access token found inside the Claude app's decrypted token cache. <see cref="ToString"/> never includes the token.</summary>
public sealed class TokenCandidate
{
    /// <summary>Key path inside the cache (keys and array indices). Holds no secret values.</summary>
    public required List<string> Path { get; init; }
    public required string AccessToken { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    /// <summary>Lowercased UUIDs mentioned in the path or alongside the token.</summary>
    public HashSet<string> Uuids { get; init; } = new();

    public string PathString => string.Join(" › ", Path);
    public bool IsExpired(DateTimeOffset now) => ExpiresAt is { } e && e <= now;
    public bool Mentions(string? uuid) => !string.IsNullOrEmpty(uuid) && Uuids.Contains(uuid.ToLowerInvariant());

    public override string ToString() =>
        $"TokenCandidate(path: {DesktopTokenParser.MaskedPath(Path)}, tokenLength: {AccessToken.Length}, " +
        $"expiresAt: {(ExpiresAt is { } e ? e.ToString("u", CultureInfo.InvariantCulture) : "none")}, uuids: {Uuids.Count})";
}

/// <summary>The Claude app's sign-in settings from its <c>config.json</c>.</summary>
public sealed record DesktopConfig(string? LastKnownAccountUuid, List<KeyValuePair<string, string>> EncryptedCaches)
{
    public static DesktopConfig Parse(byte[] data)
    {
        try
        {
            using var doc = JsonDocument.Parse(ClaudeCodeCredentials.StripBom(data));
            var caches = new List<KeyValuePair<string, string>>();
            foreach (var key in new[] { "oauth:tokenCacheV2", "oauth:tokenCache" })
                if (Json.String(doc.RootElement, key) is { Length: > 0 } s) caches.Add(new(key, s));
            return new DesktopConfig(Json.String(doc.RootElement, "lastKnownAccountUuid"), caches);
        }
        catch (JsonException) { throw new CredentialException(CredentialError.Malformed); }
    }
}

/// <summary>Finds and orders access tokens in the Claude app's token cache; ported from the Mac's DesktopTokenParser.</summary>
public static partial class DesktopTokenParser
{
    static readonly string[] TokenKeys = { "accessToken", "access_token", "token" };
    static readonly string[] ExpiryKeys = { "expiresAt", "expires_at", "expiry", "expires", "expiration", "expirationTime", "expiresOn", "expiry_date", "exp" };

    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex UuidRegex();

    /// <summary>Walks any JSON value for objects carrying an access token. Embedded JSON strings are parsed too.</summary>
    public static List<TokenCandidate> Candidates(JsonNode? json)
    {
        var out_ = new List<TokenCandidate>();
        Walk(json, new List<string>(), new HashSet<string>(), 0, out_);
        return out_;
    }

    public static JsonNode? ParseJson(byte[] data)
    {
        try
        {
            var v = JsonNode.Parse(data);
            if (v is JsonValue s && s.TryGetValue<string>(out var str))
                try { return JsonNode.Parse(str) ?? v; } catch (JsonException) { return v; }
            return v;
        }
        catch (JsonException) { return null; }
    }

    static void Walk(JsonNode? node, List<string> path, HashSet<string> inherited, int depth, List<TokenCandidate> out_)
    {
        if (depth >= 32) return;
        if (node is JsonObject dict)
        {
            var context = new HashSet<string>(inherited);
            if (path.Count > 0) context.UnionWith(Uuids(path[^1]));
            foreach (var (_, v) in dict)
                if (v is JsonValue jv && jv.TryGetValue<string>(out var s)) context.UnionWith(Uuids(s, wholeOnly: true));
            foreach (var key in TokenKeys)
                if (dict[key] is JsonValue tv && tv.TryGetValue<string>(out var t) && t.Length >= 20)
                    out_.Add(new TokenCandidate { Path = path.Append(key).ToList(), AccessToken = t, ExpiresAt = Expiry(dict), Uuids = context });
            foreach (var key in dict.Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal))
            {
                var v = dict[key];
                if (TokenKeys.Contains(key) && v is JsonValue vv && vv.TryGetValue<string>(out _)) continue;
                WalkChild(v, path.Append(key).ToList(), context, depth, out_);
            }
        }
        else if (node is JsonArray arr)
            for (var i = 0; i < arr.Count; i++) WalkChild(arr[i], path.Append($"[{i}]").ToList(), inherited, depth, out_);
    }

    static void WalkChild(JsonNode? v, List<string> path, HashSet<string> inherited, int depth, List<TokenCandidate> out_)
    {
        if (v is JsonValue jv && jv.TryGetValue<string>(out var s))
        {
            if (EmbeddedJson(s) is { } nested) Walk(nested, path, inherited, depth + 1, out_);
        }
        else Walk(v, path, inherited, depth + 1, out_);
    }

    static JsonNode? EmbeddedJson(string s)
    {
        var t = s.Trim();
        if (!(t.StartsWith('{') || t.StartsWith('['))) return null;
        try { return JsonNode.Parse(t); } catch (JsonException) { return null; }
    }

    static DateTimeOffset? Expiry(JsonObject dict)
    {
        foreach (var key in ExpiryKeys)
        {
            if (dict[key] is not JsonValue v) continue;
            if (v.GetValueKind() == JsonValueKind.Number) return EpochDate(Number(v));
            if (v.TryGetValue<string>(out var s))
            {
                if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return EpochDate(d);
                if (IsoDate.Parse(s) is { } iso) return iso;
            }
        }
        return null;
    }

    /// <summary>A JSON number as a double, whether it was parsed or built in memory.</summary>
    static double Number(JsonValue v) => double.Parse(v.ToJsonString(), CultureInfo.InvariantCulture);

    static DateTimeOffset? EpochDate(double v) => v switch
    {
        > 1e14 => DateTimeOffset.FromUnixTimeMilliseconds((long)(v / 1000)),   // microseconds
        > 1e11 => DateTimeOffset.FromUnixTimeMilliseconds((long)v),            // milliseconds
        > 1e8 => DateTimeOffset.FromUnixTimeMilliseconds((long)(v * 1000)),    // seconds
        _ => null,
    };

    static HashSet<string> Uuids(string s, bool wholeOnly = false)
    {
        if (wholeOnly) return s.Length == 36 && Guid.TryParse(s, out _) ? new() { s.ToLowerInvariant() } : new();
        return UuidRegex().Matches(s).Select(m => m.Value.ToLowerInvariant()).ToHashSet();
    }

    /// <summary>
    /// Orders candidates for the Claude app account: a uuid match first, then the cached choice only while it is
    /// unexpired, then the rest. Within a group, unexpired before expired, then the latest expiry first.
    /// </summary>
    public static List<TokenCandidate> Ordered(IEnumerable<TokenCandidate> cands, string? accountUuid, string? cachedPath, DateTimeOffset now)
    {
        int Rank(TokenCandidate c) => c.Mentions(accountUuid) ? 0 : cachedPath is not null && c.PathString == cachedPath && !c.IsExpired(now) ? 1 : 2;
        return cands.OrderBy(Rank).ThenBy(c => c.IsExpired(now)).ThenByDescending(c => c.ExpiresAt ?? DateTimeOffset.MinValue).ToList();
    }

    // MARK: Shape dump (key names and types only)

    /// <summary>Renders key names and value types, never values. UUIDs in keys are shortened; opaque runs are masked.</summary>
    public static string Shape(JsonNode? json)
    {
        var lines = new List<string>();
        Shape(json, 0, 0, lines);
        return string.Join("\n", lines);
    }

    static void Shape(JsonNode? node, int indent, int depth, List<string> lines)
    {
        var pad = new string(' ', indent * 2);
        if (depth >= 24) { lines.Add(pad + "…"); return; }
        if (node is JsonObject dict)
        {
            foreach (var key in dict.Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal))
            {
                var v = dict[key];
                lines.Add(pad + MaskedKey(key) + ": " + TypeDescription(v));
                if (v is JsonValue jv && jv.TryGetValue<string>(out var s) && EmbeddedJson(s) is { } nested) Shape(nested, indent + 1, depth + 1, lines);
                else if (v is JsonObject or JsonArray) Shape(v, indent + 1, depth + 1, lines);
            }
        }
        else if (node is JsonArray { Count: > 0 } arr)
        {
            lines.Add(pad + "[0]: " + TypeDescription(arr[0]) + (arr.Count > 1 ? $"  (showing first of {arr.Count})" : ""));
            if (arr[0] is JsonObject or JsonArray) Shape(arr[0], indent + 1, depth + 1, lines);
        }
    }

    public static string TypeDescription(JsonNode? v)
    {
        switch (v)
        {
            case null: return "null";
            case JsonObject o: return $"object({o.Count} keys)";
            case JsonArray a: return $"array({a.Count})";
        }
        var jv = (JsonValue)v;
        switch (jv.GetValueKind())
        {
            case JsonValueKind.String:
                var s = jv.GetValue<string>();
                var hints = new List<string>();
                if (EmbeddedJson(s) is not null) hints.Add("embedded JSON");
                else if (Guid.TryParse(s, out _) && s.Length == 36) hints.Add("uuid");
                else if (IsoDate.Parse(s) is not null) hints.Add("ISO date");
                else if (s.StartsWith("sk-ant-", StringComparison.Ordinal)) hints.Add("sk-ant token");
                else if (s.Split('.').Length == 3 && s.Length > 40) hints.Add("JWT-like");
                else if (s.Length >= 32 && !s.Contains(' ')) hints.Add("opaque");
                return $"string(len {s.Length}" + (hints.Count == 0 ? "" : ", " + string.Join(", ", hints)) + ")";
            case JsonValueKind.True or JsonValueKind.False: return "bool";
            case JsonValueKind.Number:
                var d = Number(jv);
                var t = d == Math.Round(d) ? "number(int)" : "number(float)";
                if (d is > 1e11 and < 1e14) t += " epoch-ms-like";
                else if (d is > 1e8 and < 1e11) t += " epoch-s-like";
                return t;
            default: return "null";
        }
    }

    public static string MaskedKey(string key)
    {
        if (key.StartsWith("sk-ant", StringComparison.Ordinal)) return "<secret-like key>";
        var sb = new StringBuilder();
        var last = 0;
        foreach (Match m in UuidRegex().Matches(key))
        {
            sb.Append(MaskOpaque(key[last..m.Index]));
            sb.Append($"<uuid {m.Value[..8].ToLowerInvariant()}…>");
            last = m.Index + m.Length;
        }
        sb.Append(MaskOpaque(key[last..]));
        return sb.ToString();
    }

    /// <summary>Replaces long runs of token-ish characters (32+) with a length marker.</summary>
    static string MaskOpaque(string s)
    {
        var sb = new StringBuilder();
        var run = new StringBuilder();
        void Flush()
        {
            sb.Append(run.Length >= 32 ? $"<opaque len {run.Length}>" : run.ToString());
            run.Clear();
        }
        foreach (var ch in s)
        {
            if (char.IsLetterOrDigit(ch) || ch is '_' or '-' or '=' or '+' or '/') run.Append(ch);
            else { Flush(); sb.Append(ch); }
        }
        Flush();
        return sb.ToString();
    }

    public static string MaskedPath(IEnumerable<string> path) => string.Join(" › ", path.Select(MaskedKey));
}

/// <summary>
/// Chromium's Windows scheme, which Electron's safeStorage uses: the AES-256 key sits DPAPI-wrapped in
/// <c>Local State</c> (<c>os_crypt.encrypted_key</c>, base64 of "DPAPI" + blob); each value is "v10" + 12-byte nonce +
/// ciphertext + 16-byte GCM tag. The DPAPI unwrap itself lives in the app (it needs Windows).
/// </summary>
public static class ChromiumWin
{
    public static byte[] EncryptedKey(byte[] localState)
    {
        try
        {
            using var doc = JsonDocument.Parse(ClaudeCodeCredentials.StripBom(localState));
            var b64 = Json.String(Json.Object(doc.RootElement, "os_crypt"), "encrypted_key") ?? throw new CredentialException(CredentialError.NotFound);
            return Convert.FromBase64String(b64);
        }
        catch (Exception e) when (e is JsonException or FormatException) { throw new CredentialException(CredentialError.Malformed); }
    }

    public static byte[] StripDpapiPrefix(byte[] wrapped) =>
        wrapped.Length > 5 && wrapped.AsSpan(0, 5).SequenceEqual("DPAPI"u8) ? wrapped[5..] : throw new CredentialException(CredentialError.Malformed);

    public static byte[] Decrypt(byte[] blob, byte[] key)
    {
        if (blob.Length < 3 + 12 + 16 || !blob.AsSpan(0, 3).SequenceEqual("v10"u8)) throw new CredentialException(CredentialError.DecryptFailed);
        var nonce = blob.AsSpan(3, 12);
        var cipher = blob.AsSpan(15, blob.Length - 15 - 16);
        var tag = blob.AsSpan(blob.Length - 16);
        var plain = new byte[cipher.Length];
        try
        {
            using var gcm = new AesGcm(key, 16);
            gcm.Decrypt(nonce, cipher, tag, plain);
        }
        catch (CryptographicException) { throw new CredentialException(CredentialError.DecryptFailed); }
        return plain;
    }

    public static byte[] DecryptBase64(string value, byte[] key)
    {
        try { return Decrypt(Convert.FromBase64String(value), key); }
        catch (FormatException) { throw new CredentialException(CredentialError.DecryptFailed); }
    }

    /// <summary>Newer Chromium prepends SHA-256(host) to a cookie's plaintext; drop it when present.</summary>
    public static byte[] StripHostDigest(byte[] plaintext, string host)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(host));
        return plaintext.Length >= digest.Length && plaintext.AsSpan(0, digest.Length).SequenceEqual(digest) ? plaintext[digest.Length..] : plaintext;
    }
}
