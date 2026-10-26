using System.Net;
using UsageBar.Core;

namespace UsageBar.Tests;

/// <summary>Damaged or unexpected settings.json content: bad accounts are set aside, never the good ones.</summary>
public class SettingsRobustnessTests
{
    const string Good = """{"id":"00000000-0000-4000-8000-0000000000aa","name":"Personal","tag":"P","colour":"orange","source":{"kind":"claudeCode","dir":"~/.claude"}}""";

    static AppSettings Load(string accounts) => AppSettings.FromJson("{\"accounts\":[" + accounts + "]}")!;

    [Fact]
    public void ANullAccountIsDropped()
    {
        var s = Load("null," + Good);
        Assert.Equal(Guid.Parse("00000000-0000-4000-8000-0000000000aa"), Assert.Single(s.Accounts).Id);
        Assert.Empty(Load("null").Accounts);
    }

    [Theory]
    [InlineData("""{"id":"00000000-0000-4000-8000-0000000000bb","name":"W","tag":"W","source":null}""")]
    [InlineData("""{"id":"00000000-0000-4000-8000-0000000000bb","name":"W","tag":"W","source":{"kind":"safariSession"}}""")]
    [InlineData("""{"id":"not-a-guid","name":"W","tag":"W","source":{"kind":"claudeDesktop"}}""")]
    [InlineData("""{"id":"00000000-0000-4000-8000-0000000000bb","name":"W","tag":"W"}""")]
    [InlineData("\"just a string\"")]
    public void AnUnreadableAccountLeavesTheOthersAndIsWrittenBack(string bad)
    {
        var s = Load(Good + "," + bad);
        Assert.Equal(Guid.Parse("00000000-0000-4000-8000-0000000000aa"), Assert.Single(s.Accounts).Id);
        // Saving must not throw, and keeps the account it couldn't read for a build that can.
        var json = s.ToJson();
        var again = AppSettings.FromJson(json)!;
        Assert.Single(again.Accounts);
        Assert.Contains(System.Text.Json.Nodes.JsonNode.Parse(bad)!.ToJsonString(), System.Text.Json.Nodes.JsonNode.Parse(json)!["accounts"]!.ToJsonString());
    }

    [Fact]
    public void MissingNameOrTagGetDefaultsInsteadOfNulls()
    {
        var a = Assert.Single(Load("""{"id":"00000000-0000-4000-8000-0000000000aa","name":null,"source":{"kind":"claudeCode"}}""").Accounts);
        Assert.False(string.IsNullOrEmpty(a.Name));
        Assert.False(string.IsNullOrEmpty(a.Tag));
    }

    [Fact]
    public void OnlyAMissingAccountListFallsBackToPersonal()
    {
        Assert.Equal(new[] { "P" }, AppSettings.FromJson("{}")!.Accounts.Select(a => a.Tag));
        Assert.Empty(AppSettings.FromJson("""{"accounts":[]}""")!.Accounts);
        // Present but not a list, or null: no accounts (Claude Code may not even be signed in), never a made-up Personal.
        Assert.Empty(AppSettings.FromJson("""{"accounts":7}""")!.Accounts);
        Assert.Empty(AppSettings.FromJson("""{"accounts":null}""")!.Accounts);
    }
}

/// <summary>Reads of other apps' files never block their writes, and odd file states give an issue, not an exception.</summary>
public class SharedReadTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "usagebar-share-" + Guid.NewGuid().ToString("N"));

    public SharedReadTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    [Fact]
    public void AnOpenReadLetsTheOwnerRewriteDeleteOrMoveTheFile()
    {
        // A rename *over* an open file fails on Windows whatever the reader shares (only POSIX-style renames get
        // through), so that case relies on the read being brief; everything else must go through.
        var path = Path.Combine(dir, "config.json");
        File.WriteAllText(path, "{}");
        using (Paths.OpenShared(path)) File.WriteAllText(path, "{\"a\":1}");      // Claude Code rewrites in place
        Assert.Equal("{\"a\":1}", File.ReadAllText(path));
        using (Paths.OpenShared(path)) File.Move(path, path + ".old");            // renamed away
        Assert.True(File.Exists(path + ".old"));
        using (Paths.OpenShared(path + ".old")) File.Delete(path + ".old");       // deleted
        Assert.False(File.Exists(path + ".old"));
    }

    [Fact]
    public void TheOldShareModeBlockedTheRewrite()
    {
        // The control for the test above: File.ReadAllBytes' sharing (read only) stops an in-place rewrite.
        var path = Path.Combine(dir, "c.json");
        File.WriteAllText(path, "{}");
        using var old = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.ThrowsAny<IOException>(() => File.WriteAllText(path, "x"));
    }

    [Fact]
    public void ALockedCredentialsFileIsACredentialErrorNotAnIOException()
    {
        var path = Path.Combine(dir, ClaudeCodeCredentials.FileName);
        File.WriteAllText(path, "{}");
        using var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var e = Assert.Throws<CredentialException>(() => ClaudeCodeCredentials.Read(dir));
        Assert.Equal(CredentialError.Denied, e.Error);
    }

    [Fact]
    public void AnOutOfRangeExpiryIsMalformed()
    {
        var raw = """{"claudeAiOauth":{"accessToken":"t","expiresAt":1e30}}"""u8.ToArray();
        Assert.Equal(CredentialError.Malformed, Assert.Throws<CredentialException>(() => ClaudeCodeCredentials.Parse(raw)).Error);
        var neg = """{"claudeAiOauth":{"accessToken":"t","expiresAt":-1e30}}"""u8.ToArray();
        Assert.Equal(CredentialError.Malformed, Assert.Throws<CredentialException>(() => ClaudeCodeCredentials.Parse(neg)).Error);
    }
}

/// <summary>Where Claude Code keeps its account file (checked on Windows 11: inside a folder named by CLAUDE_CONFIG_DIR).</summary>
public class ClaudeAccountInfoPathTests
{
    static readonly string Home = Paths.Expand("~");

    [Fact]
    public void TheDefaultFolderUsesTheFileBesideIt()
    {
        Assert.Equal(Path.Combine(Home, ".claude.json"), Paths.ClaudeAccountInfo("~/.claude", envConfigDir: null));
        Assert.Equal(Path.Combine(Home, ".claude.json"), Paths.ClaudeAccountInfo(Path.Combine(Home, ".claude") + Path.DirectorySeparatorChar, null));
    }

    [Fact]
    public void AFolderFromClaudeConfigDirKeepsItInside()
    {
        var custom = Path.Combine(Path.GetTempPath(), "cc-config");
        Assert.Equal(Path.Combine(custom, ".claude.json"), Paths.ClaudeAccountInfo(custom, envConfigDir: custom));
        Assert.Equal(Path.Combine(custom, ".claude.json"), Paths.ClaudeAccountInfo(custom + Path.DirectorySeparatorChar, custom));
        // CLAUDE_CONFIG_DIR pointing at ~/.claude itself: Claude Code then writes inside it.
        Assert.Equal(Path.Combine(Home, ".claude", ".claude.json"), Paths.ClaudeAccountInfo("~/.claude", Path.Combine(Home, ".claude")));
    }

    [Fact]
    public void FirstRunDetectionReadsTheAccountFileInsideAConfigDir()
    {
        var root = Path.Combine(Path.GetTempPath(), "usagebar-ccdir-" + Guid.NewGuid().ToString("N"));
        var cfg = Path.Combine(root, "cfg");
        var app = Path.Combine(root, "Claude");
        Directory.CreateDirectory(cfg);
        Directory.CreateDirectory(app);
        try
        {
            File.WriteAllText(Path.Combine(cfg, ".credentials.json"), "x");
            File.WriteAllText(Path.Combine(cfg, ".claude.json"), """{"oauthAccount":{"accountUuid":"00000000-0000-4000-8000-0000000000aa"}}""");
            File.WriteAllText(Path.Combine(app, "config.json"), """{"oauth:tokenCacheV2":"djEw","lastKnownAccountUuid":"00000000-0000-4000-8000-0000000000aa"}""");
            var found = SignInDetector.Detect(cfg, Paths.ClaudeAccountInfo(cfg, cfg), app);
            // Same person in both: only Personal, never a second account for the same login.
            Assert.Equal(new[] { SourceKind.ClaudeCode }, found.Kinds);
        }
        finally { Directory.Delete(root, true); }
    }
}

/// <summary>UsageClient and AccountFetcher against a stub server: what is sent, and how each answer is shown.</summary>
public class FetchTests : IDisposable
{
    sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(answer(request));
        }
    }

    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-26T06:30:00Z");
    const string UsageBody = """{"five_hour":{"utilization":20,"resets_at":"2026-10-26T08:00:00Z"},"seven_day":{"utilization":60,"resets_at":"2026-10-30T08:00:00Z"}}""";

    readonly string dir = Path.Combine(Path.GetTempPath(), "usagebar-fetch-" + Guid.NewGuid().ToString("N"));

    public FetchTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    void Credentials(long? expiresMs = null) =>
        File.WriteAllText(Path.Combine(dir, ClaudeCodeCredentials.FileName),
                          $$$"""{"claudeAiOauth":{"accessToken":"test-token","expiresAt":{{{expiresMs ?? Now.AddHours(5).ToUnixTimeMilliseconds()}}}}}""");

    static HttpResponseMessage Answer(HttpStatusCode code, string body = "{}", string? retryAfter = null)
    {
        var r = new HttpResponseMessage(code) { Content = new StringContent(body) };
        if (retryAfter is not null) r.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        return r;
    }

    async Task<(FetchReport Report, Stub Stub)> Fetch(Func<HttpRequestMessage, HttpResponseMessage> answer)
    {
        var stub = new Stub(answer);
        var report = await new AccountFetcher(new HttpClient(stub)).FetchClaudeCodeAsync(dir, Now, default);
        return (report, stub);
    }

    [Fact]
    public async Task SendsTheOAuthHeaders()
    {
        Credentials();
        var (r, stub) = await Fetch(_ => Answer(HttpStatusCode.OK, UsageBody));
        var req = Assert.Single(stub.Requests);
        Assert.Equal(UsageClient.UsageUrl, req.RequestUri);
        Assert.Equal("Bearer", req.Headers.Authorization?.Scheme);
        Assert.Equal("test-token", req.Headers.Authorization?.Parameter);
        Assert.Equal("oauth-2025-04-20", Assert.Single(req.Headers.GetValues("anthropic-beta")));
        Assert.StartsWith("claude-cli/", req.Headers.UserAgent.ToString());
        Assert.Null(r.Issue);
        Assert.Equal(2, r.Snapshot!.Limits.Count);
    }

    [Fact]
    public async Task EachErrorAnswerBecomesItsIssue()
    {
        Credentials();
        Assert.Equal(AccountFetcher.ClaudeCodeRejected, (await Fetch(_ => Answer(HttpStatusCode.Unauthorized))).Report.Issue);
        Assert.Equal(AccountIssue.Scope, (await Fetch(_ => Answer(HttpStatusCode.Forbidden, """{"error":{"type":"forbidden","message":"missing scope user:profile"}}"""))).Report.Issue);
        Assert.Equal(AccountFetcher.ClaudeCodeRejected, (await Fetch(_ => Answer(HttpStatusCode.Forbidden, """{"error":"nope"}"""))).Report.Issue);
        Assert.Equal(AccountIssue.Http(500), (await Fetch(_ => Answer(HttpStatusCode.InternalServerError))).Report.Issue);
    }

    [Fact]
    public async Task RateLimitsPauseFromNowForRetryAfterOrFiveMinutesCappedAtADay()
    {
        Credentials();
        Assert.Equal(AccountIssue.RateLimited(Now.AddSeconds(120)), (await Fetch(_ => Answer(HttpStatusCode.TooManyRequests, retryAfter: "120"))).Report.Issue);
        Assert.Equal(AccountIssue.RateLimited(Now.AddSeconds(300)), (await Fetch(_ => Answer(HttpStatusCode.TooManyRequests))).Report.Issue);
        Assert.Equal(AccountIssue.RateLimited(Now.AddDays(1)), (await Fetch(_ => Answer(HttpStatusCode.TooManyRequests, retryAfter: "1e12"))).Report.Issue);
    }

    [Fact]
    public async Task NoUsableLoginMakesNoRequest()
    {
        var (missing, s1) = await Fetch(_ => Answer(HttpStatusCode.OK, UsageBody));
        Assert.Equal(AccountFetcher.ClaudeCodeMissing, missing.Issue);

        Credentials(Now.AddMinutes(-1).ToUnixTimeMilliseconds());
        var (expired, s2) = await Fetch(_ => Answer(HttpStatusCode.OK, UsageBody));
        Assert.Equal(AccountIssue.ExpiredCli, expired.Issue);

        File.WriteAllText(Path.Combine(dir, ClaudeCodeCredentials.FileName), "{ not json");
        var (malformed, s3) = await Fetch(_ => Answer(HttpStatusCode.OK, UsageBody));
        Assert.Equal(IssueKind.Rejected, malformed.Issue?.Kind);

        File.WriteAllText(Path.Combine(dir, ClaudeCodeCredentials.FileName), """{"claudeAiOauth":{"accessToken":"t","expiresAt":1e30}}""");
        var (badExpiry, s4) = await Fetch(_ => Answer(HttpStatusCode.OK, UsageBody));
        Assert.Equal(IssueKind.Rejected, badExpiry.Issue?.Kind);

        Assert.Empty(s1.Requests.Concat(s2.Requests).Concat(s3.Requests).Concat(s4.Requests));
    }

    [Fact]
    public async Task ALockedLoginFileIsAnIssueNotAnException()
    {
        Credentials();
        using var held = new FileStream(Path.Combine(dir, ClaudeCodeCredentials.FileName), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var (r, stub) = await Fetch(_ => Answer(HttpStatusCode.OK, UsageBody));
        Assert.Equal(IssueKind.Rejected, r.Issue?.Kind);
        Assert.Empty(stub.Requests);
    }
}

public class AbsurdServerValueTests
{
    [Fact]
    public void ExtraUsageWithSillyDecimalPlacesStillFormats()
    {
        foreach (var dp in new[] { 29, -3, 1000 })
            Assert.NotNull(Formatters.ExtraUsageLine(new ExtraUsage(true, 15000, 2040, 13.6, "AUD", dp, null)));
        Assert.NotNull(Formatters.ExtraUsageLine(new ExtraUsage(true, 1e300, 1e300, 1, "AUD", 2, null)));
    }

    [Fact]
    public void AFarFutureOrNegativeRateTimeFallsBackToNow()
    {
        var now = DateTimeOffset.Parse("2026-10-26T00:00:00Z");
        Assert.Equal(now, ExchangeRates.Parse("""{"rates":{"AUD":1.4},"time_last_update_unix":1e15}"""u8, now)!.AsOf);
        Assert.Equal(now, ExchangeRates.Parse("""{"rates":{"AUD":1.4},"time_last_update_unix":-1e15}"""u8, now)!.AsOf);
    }
}

public class SqliteUriTests
{
    [Fact]
    public void EverySegmentIsEncodedButTheDrive()
    {
        Assert.Equal("file:///D:/Data/a%20b/50%25/x%23y/q%3F/Cookies", ChromiumWin.SqliteFileUri(@"D:\Data\a b\50%\x#y\q?\Cookies"));
    }
}
