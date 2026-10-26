using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace UsageBar.Core;

/// <summary>
/// Answers the four Anthropic endpoints from a fixtures folder instead of the network (see the fixtures README).
/// Timestamps ending in <c>_at</c> are moved by (now − the fixtures' "now") so resets look current in the running app;
/// <see cref="ErrorMode"/> serves one of the error files on the usage endpoints to show each error state.
/// </summary>
public sealed class FixtureHandler(string dir, Func<DateTimeOffset>? clock = null, string? errorMode = null) : HttpMessageHandler
{
    /// <summary>The fixtures' fixed "now" (README: 2026-03-02T05:00:00Z).</summary>
    public static readonly DateTimeOffset FixtureNow = DateTimeOffset.Parse("2026-03-02T05:00:00Z");

    public string? ErrorMode { get; } = errorMode ?? Environment.GetEnvironmentVariable("USAGEBAR_FIXTURE_ERROR");

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri?.AbsolutePath ?? "";
        var host = request.RequestUri?.Host ?? "";
        var isUsage = path.EndsWith("/usage", StringComparison.Ordinal);
        if (isUsage && ErrorMode is { Length: > 0 } mode) return Task.FromResult(Error(mode));

        string? file = (host, path) switch
        {
            ("api.anthropic.com", "/api/oauth/usage") => "api/oauth-usage.json",
            ("api.anthropic.com", "/api/oauth/profile") => "api/oauth-profile.json",
            ("claude.ai", "/api/organizations") => "api/claude-ai-organizations.json",
            ("claude.ai", _) when isUsage => "api/claude-ai-org-usage.json",
            _ => null,
        };
        if (file is null || !File.Exists(Path.Combine(dir, file)))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
        var body = File.ReadAllText(Path.Combine(dir, file));
        if (clock is not null || isUsage) body = Rebase(body, (clock ?? (() => DateTimeOffset.UtcNow))() - FixtureNow);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
    }

    HttpResponseMessage Error(string mode)
    {
        (int status, string file, string? cf, string? retry) = mode.ToLowerInvariant() switch
        {
            "401" => (401, "api/error-401.json", (string?)null, (string?)null),
            "403" => (403, "api/error-403-scope.json", null, null),
            "429" => (429, "api/error-429.json", null, "120"),
            _ => (503, "api/cloudflare-challenge.html", "challenge", null),
        };
        var path = Path.Combine(dir, file);
        var resp = new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(File.Exists(path) ? File.ReadAllText(path) : ""),
        };
        if (cf is not null) resp.Headers.TryAddWithoutValidation("cf-mitigated", cf);
        if (retry is not null) resp.Headers.TryAddWithoutValidation("Retry-After", retry);
        return resp;
    }

    /// <summary>Moves every ISO timestamp under a key ending in <c>_at</c> by <paramref name="shift"/>.</summary>
    public static string Rebase(string json, TimeSpan shift)
    {
        var node = JsonNode.Parse(json);
        if (node is null) return json;
        void Walk(JsonNode? n)
        {
            switch (n)
            {
                case JsonObject o:
                    foreach (var key in o.Select(kv => kv.Key).ToList())
                    {
                        var v = o[key];
                        if (key.EndsWith("_at", StringComparison.Ordinal) && v is JsonValue jv && jv.TryGetValue<string>(out var s)
                            && IsoDate.Parse(s) is { } d)
                            o[key] = (d + shift).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'+00:00'");
                        else Walk(v);
                    }
                    break;
                case JsonArray a:
                    foreach (var item in a) Walk(item);
                    break;
            }
        }
        Walk(node);
        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }
}
