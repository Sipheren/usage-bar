using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace UsageBar.Core;

public enum FetchOutcomeKind { Ok, Unauthorized, ScopeError, RateLimited, Http, Undecodable }

public sealed record FetchOutcome(FetchOutcomeKind Kind, UsageSnapshot? Snapshot = null, int Status = 0, double? RetryAfter = null);

/// <summary>Talks to the usage and profile endpoints. Tokens only ever live in request headers.</summary>
public sealed class UsageClient(HttpClient http)
{
    public static readonly Uri UsageUrl = new("https://api.anthropic.com/api/oauth/usage");
    public static readonly Uri ProfileUrl = new("https://api.anthropic.com/api/oauth/profile");

    static HttpRequestMessage OAuthRequest(Uri url, string token)
    {
        var r = new HttpRequestMessage(HttpMethod.Get, url);
        r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        r.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
        r.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        r.Headers.TryAddWithoutValidation("User-Agent", ClaudeCliVersion.UserAgent);
        return r;
    }

    public async Task<FetchOutcome> FetchUsageAsync(string token, CancellationToken ct = default)
    {
        using var resp = await http.SendAsync(OAuthRequest(UsageUrl, token), ct).ConfigureAwait(false);
        var data = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        var status = (int)resp.StatusCode;
        switch (resp.StatusCode)
        {
            case HttpStatusCode.OK:
                try { return new FetchOutcome(FetchOutcomeKind.Ok, UsageDecoder.Decode(data, DateTimeOffset.UtcNow), status); }
                catch (UsageDecoder.DecodeException) { return new FetchOutcome(FetchOutcomeKind.Undecodable, Status: status); }
            case HttpStatusCode.Unauthorized:
                return new FetchOutcome(FetchOutcomeKind.Unauthorized, Status: status);
            case HttpStatusCode.Forbidden:
                return new FetchOutcome(IsScopeError(data) ? FetchOutcomeKind.ScopeError : FetchOutcomeKind.Unauthorized, Status: status);
            case HttpStatusCode.TooManyRequests:
                return new FetchOutcome(FetchOutcomeKind.RateLimited, Status: status,
                                        RetryAfter: RetryAfter(HeaderValue(resp, "Retry-After"), DateTimeOffset.UtcNow));
            default:
                return new FetchOutcome(FetchOutcomeKind.Http, Status: status);
        }
    }

    public async Task<(Profile? Profile, int Status)> FetchProfileAsync(string token, CancellationToken ct = default)
    {
        using var resp = await http.SendAsync(OAuthRequest(ProfileUrl, token), ct).ConfigureAwait(false);
        var status = (int)resp.StatusCode;
        if (resp.StatusCode != HttpStatusCode.OK) return (null, status);
        return (Profile.Decode(await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false)), status);
    }

    internal static string? HeaderValue(HttpResponseMessage resp, string name) =>
        resp.Headers.TryGetValues(name, out var v) ? v.FirstOrDefault()
        : resp.Content.Headers.TryGetValues(name, out var c) ? c.FirstOrDefault() : null;

    /// <summary>True when a 403 body indicates the token can't read usage because of its OAuth scope.</summary>
    public static bool IsScopeError(ReadOnlySpan<byte> data)
    {
        var s = Encoding.UTF8.GetString(data[..Math.Min(data.Length, 2048)]).ToLowerInvariant();
        return s.Contains("scope") || s.Contains("insufficient_permission") || s.Contains("forbidden_scope");
    }

    static readonly string[] HttpDateFormats = { "r", "dddd, dd-MMM-yy HH:mm:ss 'GMT'", "ddd MMM d HH:mm:ss yyyy" };

    /// <summary>Retry-After as delta-seconds or an HTTP date.</summary>
    public static double? RetryAfter(string? value, DateTimeOffset now)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v)) return null;
        if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var secs)) return Math.Max(0, secs);
        if (DateTimeOffset.TryParseExact(v, HttpDateFormats, CultureInfo.InvariantCulture,
                                         DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d))
            return Math.Max(0, (d - now).TotalSeconds);
        return null;
    }
}
