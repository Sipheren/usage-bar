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

    [Fact]
    public void RebaseMovesOnlyTimestampsEndingInAt()
    {
        var json = """{"resets_at":"2026-03-02T07:10:00.000000+00:00","created":"2026-03-02T07:10:00Z","n":1}""";
        var moved = FixtureHandler.Rebase(json, TimeSpan.FromDays(1));
        Assert.Contains("\"resets_at\":\"2026-03-03T07:10:00.000000+00:00\"", moved);
        Assert.Contains("\"created\":\"2026-03-02T07:10:00Z\"", moved);
    }
}
