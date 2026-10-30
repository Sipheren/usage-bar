using System.Net;
using System.Text;
using System.Text.Json;

namespace UsageBar.Core;

public sealed record ClaudeAiOrg(string Uuid, string? Name);

/// <summary><c>RateLimited</c> is HTTP 429 from either request.</summary>
public enum CookieOutcomeKind { Ok, NoSession, Cloudflare, Unauthorized, ScopeError, RateLimited, Http, Undecodable, NoOrganizations, Network }

/// <summary><paramref name="RetryAfter"/> is a 429's Retry-After in seconds, null when missing.</summary>
public sealed record CookieUsageOutcome(CookieOutcomeKind Kind, int Status = 0, UsageSnapshot? Snapshot = null, ClaudeAiOrg? Org = null,
                                        string? Detail = null, double? RetryAfter = null);

/// <summary>
/// Fetches claude.ai usage with a browser session cookie (the Claude app's, or a browser session's).
/// Read-only: never writes cookies or tokens.
/// </summary>
public sealed class ClaudeAiCookieClient(HttpClient http)
{
    public static readonly Uri OrgsUrl = new("https://claude.ai/api/organizations");
    // A plain browser-like User-Agent; claude.ai rejects obviously non-browser agents on this surface.
    public const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36";

    /// <summary>
    /// Picks the organization to show: the <c>lastActiveOrg</c> cookie if it's one of the orgs, otherwise the first.
    /// The session shows whatever account it is signed in to.
    /// </summary>
    public static ClaudeAiOrg? PickOrg(IReadOnlyList<ClaudeAiOrg> orgs, string? lastActiveOrg)
    {
        if (orgs.Count == 0) return null;
        if (lastActiveOrg is not null && orgs.FirstOrDefault(o => string.Equals(o.Uuid, lastActiveOrg, StringComparison.OrdinalIgnoreCase)) is { } m) return m;
        return orgs[0];
    }

    public static List<ClaudeAiOrg> ParseOrganizations(ReadOnlySpan<byte> data)
    {
        var out_ = new List<ClaudeAiOrg>();
        try
        {
            using var doc = JsonDocument.Parse(data.ToArray());
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return out_;
            foreach (var o in doc.RootElement.EnumerateArray())
                if ((Json.String(o, "uuid") ?? Json.String(o, "id")) is { } uuid) out_.Add(new ClaudeAiOrg(uuid, Json.String(o, "name")));
        }
        catch (JsonException) { }
        return out_;
    }

    /// <summary>True when the response is a Cloudflare interstitial rather than the API.</summary>
    public static bool IsCloudflareChallenge(int status, string? cfMitigated, ReadOnlySpan<byte> body)
    {
        if (cfMitigated is not null && cfMitigated.Contains("challenge", StringComparison.OrdinalIgnoreCase)) return true;
        if (status is 403 or 503 or 429)
        {
            var s = Encoding.UTF8.GetString(body[..Math.Min(body.Length, 2048)]);
            return s.Contains("Just a moment") || s.Contains("cf-browser-verification") || s.Contains("challenge-platform");
        }
        return false;
    }

    /// <summary>A Cloudflare challenge, then a 429, ends the flow on either request. Null for any other response.</summary>
    public static CookieUsageOutcome? BlockedOutcome(int status, string? cfMitigated, string? retryAfter, ReadOnlySpan<byte> body, DateTimeOffset now)
    {
        if (IsCloudflareChallenge(status, cfMitigated, body)) return new(CookieOutcomeKind.Cloudflare, status);
        return status == 429 ? new(CookieOutcomeKind.RateLimited, status, RetryAfter: UsageClient.RetryAfter(retryAfter, now)) : null;
    }

    static CookieUsageOutcome? Blocked(HttpResponseMessage resp, ReadOnlySpan<byte> body) =>
        BlockedOutcome((int)resp.StatusCode, UsageClient.HeaderValue(resp, "cf-mitigated"), UsageClient.HeaderValue(resp, "Retry-After"),
                       body, DateTimeOffset.UtcNow);

    public static string CookieHeader(IReadOnlyDictionary<string, string> jar) => string.Join("; ", jar.Select(kv => kv.Key + "=" + kv.Value));

    static HttpRequestMessage Request(Uri url, string cookieHeader)
    {
        var r = new HttpRequestMessage(HttpMethod.Get, url);
        r.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        r.Headers.TryAddWithoutValidation("Accept", "application/json");
        r.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
        r.Headers.Referrer = new Uri("https://claude.ai/");
        return r;
    }

    /// <summary>Full flow: organizations, pick, usage. <paramref name="jar"/> must include <c>sessionKey</c>.</summary>
    public async Task<CookieUsageOutcome> FetchUsageAsync(IReadOnlyDictionary<string, string> jar, CancellationToken ct = default)
    {
        if (!jar.ContainsKey("sessionKey")) return new(CookieOutcomeKind.NoSession);
        var header = CookieHeader(jar);
        try
        {
            using var orgResp = await http.SendAsync(Request(OrgsUrl, header), ct).ConfigureAwait(false);
            var orgData = await orgResp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            var orgStatus = (int)orgResp.StatusCode;
            if (Blocked(orgResp, orgData) is { } orgBlocked) return orgBlocked;
            if (orgStatus is 401 or 403) return new(CookieOutcomeKind.Unauthorized, orgStatus);
            if (orgStatus != 200) return new(CookieOutcomeKind.Http, orgStatus);

            var orgs = ParseOrganizations(orgData);
            jar.TryGetValue("lastActiveOrg", out var lastActive);
            if (PickOrg(orgs, lastActive) is not { } org) return new(CookieOutcomeKind.NoOrganizations, orgStatus);

            var usageUrl = new Uri($"https://claude.ai/api/organizations/{Uri.EscapeDataString(org.Uuid)}/usage");
            using var resp = await http.SendAsync(Request(usageUrl, header), ct).ConfigureAwait(false);
            var data = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            var status = (int)resp.StatusCode;
            if (Blocked(resp, data) is { } blocked) return blocked;
            switch (resp.StatusCode)
            {
                case HttpStatusCode.OK:
                    try { return new(CookieOutcomeKind.Ok, 200, UsageDecoder.Decode(data, DateTimeOffset.UtcNow), org); }
                    catch (UsageDecoder.DecodeException) { return new(CookieOutcomeKind.Undecodable, 200); }
                case HttpStatusCode.Unauthorized:
                    return new(CookieOutcomeKind.Unauthorized, 401);
                case HttpStatusCode.Forbidden:
                    return new(UsageClient.IsScopeError(data) ? CookieOutcomeKind.ScopeError : CookieOutcomeKind.Unauthorized, 403);
                default:
                    return new(CookieOutcomeKind.Http, status);
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            return new(CookieOutcomeKind.Network, Detail: NetworkMessage.Short(e));
        }
    }
}

public static class NetworkMessage
{
    /// <summary>A short, secret-free description of a network failure.</summary>
    public static string Short(Exception e) => e switch
    {
        TaskCanceledException => "timed out",
        HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError } => "can't resolve host",
        HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError } => "can't connect",
        HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } => "TLS failed",
        HttpRequestException => "request failed",
        _ => "request failed",
    };
}
