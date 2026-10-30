using System.Text;
using UsageBar.Core;

namespace UsageBar.Tests;

public class ClaudeAiCookieClientTests
{
    static readonly List<ClaudeAiOrg> Orgs = new() { new("AAA-first", "First"), new("BBB-second", "Second") };

    [Fact]
    public void PickOrgPrefersLastActiveOrg() =>
        Assert.Equal("BBB-second", ClaudeAiCookieClient.PickOrg(Orgs, "bbb-second")?.Uuid);

    [Fact]
    public void PickOrgFallsBackToTheFirstOrg()
    {
        Assert.Equal("AAA-first", ClaudeAiCookieClient.PickOrg(Orgs, null)?.Uuid);
        Assert.Null(ClaudeAiCookieClient.PickOrg(new List<ClaudeAiOrg>(), "x"));
    }

    [Fact]
    public void PickOrgIgnoresLastActiveOrgNotInList() =>
        Assert.Equal("AAA-first", ClaudeAiCookieClient.PickOrg(Orgs, "ccc-missing")?.Uuid);

    [Fact]
    public void ParseOrganizations()
    {
        var parsed = ClaudeAiCookieClient.ParseOrganizations(Encoding.UTF8.GetBytes("""[{"uuid":"a","name":"One"},{"id":"b"},{"name":"no uuid"},{"id":1}]"""));
        Assert.Equal(new[] { new ClaudeAiOrg("a", "One"), new ClaudeAiOrg("b", null) }, parsed);
        Assert.Empty(ClaudeAiCookieClient.ParseOrganizations(Encoding.UTF8.GetBytes("{}")));
    }

    [Fact]
    public void CloudflareDetection()
    {
        Assert.True(ClaudeAiCookieClient.IsCloudflareChallenge(403, "challenge", Array.Empty<byte>()));
        Assert.True(ClaudeAiCookieClient.IsCloudflareChallenge(503, null, Encoding.UTF8.GetBytes("<title>Just a moment...</title>")));
        Assert.False(ClaudeAiCookieClient.IsCloudflareChallenge(200, null, Encoding.UTF8.GetBytes("{}")));
        Assert.False(ClaudeAiCookieClient.IsCloudflareChallenge(403, null, Encoding.UTF8.GetBytes("""{"error":"x"}""")));
    }

    [Fact]
    public void ScopeErrorDetection()
    {
        Assert.True(UsageClient.IsScopeError(Encoding.UTF8.GetBytes("""{"error":{"message":"missing scope: usage"}}""")));
        Assert.True(UsageClient.IsScopeError(Encoding.UTF8.GetBytes("""{"type":"forbidden_scope"}""")));
        Assert.False(UsageClient.IsScopeError(Encoding.UTF8.GetBytes("""{"error":"not found"}""")));
        Assert.False(UsageClient.IsScopeError(Array.Empty<byte>()));
    }

    [Fact]
    public void CookieHeaderJoinsPairs()
    {
        var header = ClaudeAiCookieClient.CookieHeader(new Dictionary<string, string> { ["sessionKey"] = "s", ["lastActiveOrg"] = "o" });
        Assert.Contains("sessionKey=s", header);
        Assert.Contains("lastActiveOrg=o", header);
        Assert.Contains("; ", header);
    }

    /// <summary>Answers each URL from a table, so the whole cookie flow runs without a network.</summary>
    sealed class TableHandler(Dictionary<string, (int Status, string Body, string? CfMitigated)> table) : HttpMessageHandler
    {
        public List<string> Requested { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.AbsoluteUri;
            Requested.Add(url);
            var (status, body, cf) = table[url];
            var resp = new HttpResponseMessage((System.Net.HttpStatusCode)status) { Content = new StringContent(body) };
            if (cf is not null) resp.Headers.TryAddWithoutValidation("cf-mitigated", cf);
            return Task.FromResult(resp);
        }
    }

    const string OrgsUrl = "https://claude.ai/api/organizations";
    const string UsageUrl = "https://claude.ai/api/organizations/AAA-first/usage";
    const string SecondUsageUrl = "https://claude.ai/api/organizations/BBB-second/usage";
    const string UsageJson = """{"five_hour":{"utilization":20,"resets_at":"2026-03-02T07:10:00Z"}}""";
    static readonly Dictionary<string, string> Jar = new() { ["sessionKey"] = "fake" };

    static async Task<(CookieUsageOutcome Outcome, TableHandler Handler)> Run(Dictionary<string, (int, string, string?)> table,
                                                                              Dictionary<string, string>? jar = null)
    {
        var h = new TableHandler(table);
        var client = new ClaudeAiCookieClient(new HttpClient(h));
        return (await client.FetchUsageAsync(jar ?? Jar), h);
    }

    static string OrgsJson => """[{"uuid":"AAA-first","name":"First"},{"uuid":"BBB-second","name":"Second"}]""";

    [Fact]
    public async Task FullFlowPicksTheFirstOrgAndDecodes()
    {
        var (o, h) = await Run(new() { [OrgsUrl] = (200, OrgsJson, null), [UsageUrl] = (200, UsageJson, null) });
        Assert.Equal(CookieOutcomeKind.Ok, o.Kind);
        Assert.Equal("First", o.Org?.Name);
        Assert.Equal(20, o.Snapshot?.Session?.Percent);
        Assert.Equal(new[] { OrgsUrl, UsageUrl }, h.Requested);
    }

    [Fact]
    public async Task FullFlowShowsTheLastActiveOrg()
    {
        var jar = new Dictionary<string, string> { ["sessionKey"] = "fake", ["lastActiveOrg"] = "BBB-second" };
        var (o, h) = await Run(new() { [OrgsUrl] = (200, OrgsJson, null), [SecondUsageUrl] = (200, UsageJson, null) }, jar);
        Assert.Equal(CookieOutcomeKind.Ok, o.Kind);
        Assert.Equal("Second", o.Org?.Name);
        Assert.Equal(new[] { OrgsUrl, SecondUsageUrl }, h.Requested);
    }

    [Fact]
    public async Task ASessionWithOneOrganizationShowsItsUsage()
    {
        // Whatever ~/.claude.json names, a session shows the account it is signed in to.
        var (o, h) = await Run(new() { [OrgsUrl] = (200, """[{"uuid":"AAA-first","name":"P"}]""", null), [UsageUrl] = (200, UsageJson, null) });
        Assert.Equal(CookieOutcomeKind.Ok, o.Kind);
        Assert.Equal(new[] { OrgsUrl, UsageUrl }, h.Requested);
    }

    [Fact]
    public async Task ErrorsMapToOutcomes()
    {
        Assert.Equal(CookieOutcomeKind.NoSession, (await Run(new(), jar: new Dictionary<string, string>())).Outcome.Kind);
        Assert.Equal(CookieOutcomeKind.Cloudflare, (await Run(new() { [OrgsUrl] = (403, "<title>Just a moment...</title>", null) })).Outcome.Kind);
        Assert.Equal(CookieOutcomeKind.Unauthorized, (await Run(new() { [OrgsUrl] = (401, "{}", null) })).Outcome.Kind);
        Assert.Equal(CookieOutcomeKind.NoOrganizations, (await Run(new() { [OrgsUrl] = (200, "[]", null) })).Outcome.Kind);
        Assert.Equal(CookieOutcomeKind.ScopeError, (await Run(new() { [OrgsUrl] = (200, OrgsJson, null), [UsageUrl] = (403, """{"error":"scope"}""", null) })).Outcome.Kind);
        Assert.Equal(CookieOutcomeKind.Cloudflare, (await Run(new() { [OrgsUrl] = (200, OrgsJson, null), [UsageUrl] = (200, "", "challenge") })).Outcome.Kind);
        var http = (await Run(new() { [OrgsUrl] = (200, OrgsJson, null), [UsageUrl] = (500, "", null) })).Outcome;
        Assert.Equal((CookieOutcomeKind.Http, 500), (http.Kind, http.Status));
        Assert.Equal(CookieOutcomeKind.Undecodable, (await Run(new() { [OrgsUrl] = (200, OrgsJson, null), [UsageUrl] = (200, "[]", null) })).Outcome.Kind);
    }
}
