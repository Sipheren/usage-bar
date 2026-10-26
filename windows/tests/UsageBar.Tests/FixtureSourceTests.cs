using UsageBar.Core;

namespace UsageBar.Tests;

/// <summary>The Work account's fixture source end to end: the real fetch code answered from the fixture files.</summary>
public class FixtureSourceTests
{
    static readonly Account Work = Account.DefaultWork() with { Source = AccountSource.Fixture(FixtureParityTests.Dir ?? "") };
    static readonly DesktopContext Ctx = new(false, new Dictionary<string, string>(), null);

    static Task<FetchReport> Fetch(string? errorMode)
    {
        var fetcher = new AccountFetcher(new HttpClient(), null, dir => new HttpClient(new FixtureHandler(dir, null, errorMode ?? "")));
        return fetcher.FetchAsync(Work, Ctx, DateTimeOffset.UtcNow);
    }

    [FixtureFact]
    public async Task ServesUsageWithResetsMovedToNow()
    {
        var r = await Fetch(null);
        Assert.Null(r.Issue);
        Assert.Equal("Example Co", r.Email);
        Assert.Equal(33, r.Snapshot!.Session!.Percent);
        // The session resets 2 h 10 min after the fixtures' "now"; moved to the real now it stays 2 h 10 min ahead.
        var ahead = r.Snapshot.Session.ResetsAt!.Value - DateTimeOffset.UtcNow;
        Assert.InRange(ahead.TotalMinutes, 129, 131);
    }

    [FixtureFact]
    public async Task ErrorFilesMapToTheirStates()
    {
        Assert.Equal(AccountIssue.NotSignedIn("Fixture sign-in rejected"), (await Fetch("401")).Issue);
        Assert.Equal(AccountIssue.Scope, (await Fetch("403")).Issue);
        Assert.Equal(AccountIssue.Http(429), (await Fetch("429")).Issue);   // the claude.ai flow, like the Mac, has no 429 pause
        Assert.Equal(IssueKind.Cloudflare, (await Fetch("cloudflare")).Issue?.Kind);
    }

    [FixtureFact]
    public async Task ErrorModesServeTheirFilesContent()
    {
        // Without this the mapping test above could pass on empty bodies from a fixtures folder missing the files.
        foreach (var (mode, status, file) in new[] { ("401", 401, "error-401.json"), ("403", 403, "error-403-scope.json"),
                                                     ("429", 429, "error-429.json"), ("cloudflare", 503, "cloudflare-challenge.html") })
        {
            var path = Path.Combine(FixtureParityTests.Dir!, "api", file);
            Assert.True(File.Exists(path), file);
            using var http = new HttpClient(new FixtureHandler(FixtureParityTests.Dir!, errorMode: mode));
            using var resp = await http.GetAsync("https://api.anthropic.com/api/oauth/usage");
            Assert.Equal(status, (int)resp.StatusCode);
            Assert.Equal(File.ReadAllText(path), await resp.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task AMissingErrorFileIsA404NotAnEmptyError()
    {
        var empty = Directory.CreateTempSubdirectory("usagebar-nofixtures").FullName;
        try
        {
            using var http = new HttpClient(new FixtureHandler(empty, errorMode: "401"));
            Assert.Equal(404, (int)(await http.GetAsync("https://api.anthropic.com/api/oauth/usage")).StatusCode);
        }
        finally { Directory.Delete(empty, true); }
    }

    [Fact]
    public void RebaseMovesOnlyTimestampsEndingInAt()
    {
        var json = """{"resets_at":"2026-03-02T07:10:00.000000+00:00","created":"2026-03-02T07:10:00Z","n":1}""";
        var moved = FixtureHandler.Rebase(json, TimeSpan.FromDays(1));
        Assert.Contains("\"resets_at\":\"2026-03-03T07:10:00.000000+00:00\"", moved);
        Assert.Contains("\"created\":\"2026-03-02T07:10:00Z\"", moved);
    }
}
