using UsageBar.Core;

namespace UsageBar.Tests;

/// <summary>
/// Each source shows the usage of whatever account it is signed in to. Nothing decides Personal or Work: the Claude
/// app's token choice asks only which token belongs to the app's own account, or failing that which one works.
/// </summary>
public class SignedInAccountTests
{
    const string AccountId = "00000000-0000-4000-8000-0000000000aa";
    const string OrgId = "00000000-0000-4000-8000-0000000000bb";
    const string OtherOrgId = "00000000-0000-4000-8000-0000000000cc";
    const string AppAccountId = "00000000-0000-4000-8000-0000000000dd";

    static TokenCandidate Candidate(string token) =>
        new() { Path = new() { "oauth:tokenCacheV2", token }, AccessToken = token, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) };

    /// <summary>Profile answers by token; anything not in the table is a 404, and "throw" is a network failure.</summary>
    static Func<string, Task<(Profile?, int)>> Profiles(Dictionary<string, (string? Account, string? Org, int Status)> table, List<string> asked) => token =>
    {
        asked.Add(token);
        if (token == "tok-network") throw new HttpRequestException("offline");
        return Task.FromResult(table.TryGetValue(token, out var r)
            ? (r.Status == 200 ? new Profile(AccountUuid: r.Account, OrganizationUuid: r.Org) : null, r.Status)
            : ((Profile?)null, 404));
    };

    [Fact]
    public async Task ProbeTakesTheFirstValidTokenWhenNoneNamesTheAppAccount()
    {
        var asked = new List<string>();
        var (chosen, matched) = await DesktopTokenParser.ProbeAsync(
            new[] { Candidate("tok-rejected"), Candidate("tok-first"), Candidate("tok-second") }, null,
            Profiles(new() { ["tok-rejected"] = (null, null, 401), ["tok-first"] = (AccountId, OrgId, 200), ["tok-second"] = (AccountId, OtherOrgId, 200) }, asked));
        Assert.Equal("tok-first", chosen?.AccessToken);
        Assert.False(matched);
    }

    [Fact]
    public async Task ProbePrefersTheTokenForTheAppsOwnAccount()
    {
        var asked = new List<string>();
        var (chosen, matched) = await DesktopTokenParser.ProbeAsync(
            new[] { Candidate("tok-other"), Candidate("tok-app") }, AppAccountId,
            Profiles(new() { ["tok-other"] = (AccountId, OtherOrgId, 200), ["tok-app"] = (AppAccountId, OrgId, 200) }, asked));
        Assert.Equal("tok-app", chosen?.AccessToken);
        Assert.True(matched);
    }

    [Fact]
    public async Task ProbeSkipsATokenWhoseProfileCannotBeReached()
    {
        var asked = new List<string>();
        var (chosen, matched) = await DesktopTokenParser.ProbeAsync(
            new[] { Candidate("tok-network"), Candidate("tok-first") }, AppAccountId,
            Profiles(new() { ["tok-first"] = (AccountId, OrgId, 200) }, asked));
        Assert.Equal("tok-first", chosen?.AccessToken);
        Assert.False(matched);
        Assert.Equal(new[] { "tok-network", "tok-first" }, asked);
    }

    [Fact]
    public async Task ProbeFindsNothingWhenNoProfileAnswers()
    {
        var (chosen, matched) = await DesktopTokenParser.ProbeAsync(
            new[] { Candidate("tok-rejected") }, AppAccountId, Profiles(new() { ["tok-rejected"] = (null, null, 401) }, new()));
        Assert.Null(chosen);
        Assert.False(matched);
    }
}
