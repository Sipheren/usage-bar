using System.Net;
using System.Text;
using UsageBar.Core;

namespace UsageBar.Tests;

/// <summary>A 429 from either endpoint pauses the account (decision 18), and the Claude app tries its cookie first.</summary>
public class RateLimitResponseTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-26T06:30:00Z");
    const string OrgId = "00000000-0000-4000-8000-0000000000dd";
    const string UsageBody = """{"five_hour":{"utilization":20,"resets_at":"2026-10-26T08:00:00Z"}}""";

    sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(answer(request));
        }
    }

    static HttpResponseMessage Answer(HttpStatusCode code, string body = "{}", string? retryAfter = null)
    {
        var r = new HttpResponseMessage(code) { Content = new StringContent(body) };
        if (retryAfter is not null) r.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        return r;
    }

    static HttpResponseMessage Orgs() => Answer(HttpStatusCode.OK, $$"""[{"uuid":"{{OrgId}}","name":"Example Co"}]""");

    static async Task<CookieUsageOutcome> CookieFetch(Func<HttpRequestMessage, HttpResponseMessage> answer) =>
        await new ClaudeAiCookieClient(new HttpClient(new Stub(answer)))
            .FetchUsageAsync(new Dictionary<string, string> { ["sessionKey"] = "test" }, null);

    [Fact]
    public void ClaudeAi429IsRateLimitedWithItsRetryAfter()
    {
        var zero = ClaudeAiCookieClient.BlockedOutcome(429, null, "0", "{}"u8, Now);
        Assert.Equal(CookieOutcomeKind.RateLimited, zero?.Kind);
        Assert.Equal(0, zero?.RetryAfter);
        var seconds = ClaudeAiCookieClient.BlockedOutcome(429, null, "120", ""u8, Now);
        Assert.Equal(CookieOutcomeKind.RateLimited, seconds?.Kind);
        Assert.Equal(120, seconds?.RetryAfter);
        var missing = ClaudeAiCookieClient.BlockedOutcome(429, null, null, ""u8, Now);
        Assert.Equal(CookieOutcomeKind.RateLimited, missing?.Kind);
        Assert.Null(missing?.RetryAfter);
    }

    [Fact]
    public void CloudflareIsCheckedBeforeA429()
    {
        Assert.Equal(CookieOutcomeKind.Cloudflare, ClaudeAiCookieClient.BlockedOutcome(429, null, null, "<title>Just a moment...</title>"u8, Now)?.Kind);
        Assert.Equal(CookieOutcomeKind.Cloudflare, ClaudeAiCookieClient.BlockedOutcome(429, "challenge", "0", ""u8, Now)?.Kind);
    }

    [Fact]
    public void OtherStatusesAreLeftToTheCaller()
    {
        foreach (var status in new[] { 200, 401, 403, 500 })
            Assert.Null(ClaudeAiCookieClient.BlockedOutcome(status, null, null, "{}"u8, Now));
    }

    [Fact]
    public async Task A429OnEitherClaudeAiRequestIsRateLimited()
    {
        var onOrgs = await CookieFetch(_ => Answer(HttpStatusCode.TooManyRequests, retryAfter: "0"));
        Assert.Equal(CookieOutcomeKind.RateLimited, onOrgs.Kind);
        Assert.Equal(429, onOrgs.Status);
        Assert.Equal(0, onOrgs.RetryAfter);

        var onUsage = await CookieFetch(r => r.RequestUri == ClaudeAiCookieClient.OrgsUrl ? Orgs() : Answer(HttpStatusCode.TooManyRequests, retryAfter: "90"));
        Assert.Equal(CookieOutcomeKind.RateLimited, onUsage.Kind);
        Assert.Equal(90, onUsage.RetryAfter);

        var ok = await CookieFetch(r => r.RequestUri == ClaudeAiCookieClient.OrgsUrl ? Orgs() : Answer(HttpStatusCode.OK, UsageBody));
        Assert.Equal(CookieOutcomeKind.Ok, ok.Kind);
    }

    [Fact]
    public void Cookie429WaitsFiveMinutesWhenRetryAfterIsZero()
    {
        var report = new FetchReport();
        AccountFetcher.ApplyCookieOutcome(new(CookieOutcomeKind.RateLimited, 429, RetryAfter: 0), report, AccountIssue.ExpiredDesktop,
                                          "the Claude app", "The Claude app", Now);
        Assert.Equal(AccountIssue.RateLimited(Now.AddSeconds(300)), report.Issue);
        Assert.Equal(0, report.RetryAfter);
    }

    [Fact]
    public void Cookie429HonoursRetryAfterUpToAnHour()
    {
        var report = new FetchReport();
        AccountFetcher.ApplyCookieOutcome(new(CookieOutcomeKind.RateLimited, 429, RetryAfter: 90), report, AccountIssue.ExpiredDesktop,
                                          "the Claude app", "The Claude app", Now);
        Assert.Equal(AccountIssue.RateLimited(Now.AddSeconds(90)), report.Issue);
        AccountFetcher.ApplyCookieOutcome(new(CookieOutcomeKind.RateLimited, 429, RetryAfter: 7200), report, AccountIssue.ExpiredDesktop,
                                          "the Claude app", "The Claude app", Now);
        Assert.Equal(AccountIssue.RateLimited(Now.AddHours(1)), report.Issue);
    }

    [Fact]
    public void OAuth429WithRetryAfterZeroOrMissingWaitsFiveMinutes()
    {
        var report = new FetchReport();
        AccountFetcher.Apply(new FetchOutcome(FetchOutcomeKind.RateLimited, Status: 429, RetryAfter: 0), report, AccountIssue.ExpiredCli, Now);
        Assert.Equal(AccountIssue.RateLimited(Now.AddSeconds(300)), report.Issue);
        Assert.Equal(0, report.RetryAfter);
        AccountFetcher.Apply(new FetchOutcome(FetchOutcomeKind.RateLimited, Status: 429), report, AccountIssue.ExpiredCli, Now);
        Assert.Equal(AccountIssue.RateLimited(Now.AddSeconds(300)), report.Issue);
    }

    [Fact]
    public void ClaudeAppCookieResultsThatStandAndThoseThatTryTheToken()
    {
        foreach (var kind in new[] { CookieOutcomeKind.Ok, CookieOutcomeKind.RateLimited, CookieOutcomeKind.Network, CookieOutcomeKind.SignedInToPersonal })
            Assert.True(AccountFetcher.DesktopCookieResultStands(kind), kind.ToString());
        foreach (var kind in new[] { CookieOutcomeKind.Unauthorized, CookieOutcomeKind.NoSession, CookieOutcomeKind.NoOrganizations, CookieOutcomeKind.Cloudflare,
                                     CookieOutcomeKind.ScopeError, CookieOutcomeKind.Http, CookieOutcomeKind.Undecodable })
            Assert.False(AccountFetcher.DesktopCookieResultStands(kind), kind.ToString());
        // Every kind is in one list or the other.
        Assert.Equal(11, Enum.GetValues<CookieOutcomeKind>().Length);
    }
}
