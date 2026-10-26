using System.Text;
using UsageBar.Core;

namespace UsageBar.Tests;

public class UsageDecodingTests
{
    // Shape from the spec, values made up.
    const string Fixture = """
    {
      "five_hour":  {"utilization": 33.0, "resets_at": "2026-03-02T08:20:00.393827+00:00"},
      "seven_day":  {"utilization": 77.0, "resets_at": "2026-03-04T15:00:00.393846+00:00"},
      "seven_day_opus": null,
      "seven_day_oauth_apps": null,
      "extra_usage": {"is_enabled": false, "monthly_limit": 10000, "used_credits": 1250.0,
                      "utilization": 12.5, "currency": "AUD", "decimal_places": 2,
                      "disabled_reason": "out_of_credits"},
      "limits": [
        {"kind": "session", "group": "session", "percent": 33, "severity": "normal",
         "resets_at": "2026-03-02T08:20:00.393827+00:00", "scope": null, "is_active": false},
        {"kind": "weekly_all", "group": "weekly", "percent": 77, "severity": "critical",
         "resets_at": "2026-03-04T15:00:00.393846+00:00", "scope": null, "is_active": true},
        {"kind": "weekly_scoped", "group": "weekly", "percent": 5, "severity": "normal",
         "resets_at": "2026-03-04T15:00:00+00:00",
         "scope": {"model": {"id": null, "display_name": "Fable"}, "surface": null}, "is_active": false},
        {"kind": "something_new", "group": "monthly", "percent": 9},
        "not an object"
      ],
      "unknown_future_key": {"anything": [1, 2, 3]}
    }
    """;

    static UsageSnapshot Decode(string json) => UsageDecoder.Decode(Encoding.UTF8.GetBytes(json), DateTimeOffset.UtcNow);

    [Fact]
    public void DecodesLimitsInOrderWithNames()
    {
        var snap = Decode(Fixture);
        Assert.Equal(new[] { "Session", "Weekly", "Fable weekly" }, snap.Ordered.Select(l => l.Name));
        Assert.Equal(33, snap.Session?.Percent);
        Assert.Equal(77, snap.Weekly?.Percent);
        Assert.Equal(5, snap.Scoped.First().Percent);
        Assert.Equal("critical", snap.Weekly?.Severity);
        Assert.Equal(300, snap.Session?.WindowMinutes);
        Assert.Equal(10080, snap.Weekly?.WindowMinutes);
        Assert.Equal(10080, snap.Scoped.First().WindowMinutes);
    }

    [Fact]
    public void ParsesSixDigitFractionalSecondsWithOffset()
    {
        var snap = Decode(Fixture);
        var expected = DateTimeOffset.Parse("2026-03-02T08:20:00Z").ToUnixTimeMilliseconds() / 1000.0 + 0.393827;
        var got = snap.Session!.ResetsAt!.Value;
        Assert.Equal(expected, got.ToUnixTimeMilliseconds() / 1000.0, 0.001);
    }

    [Fact]
    public void TwoScopedLimitsWithDistinctModelIdsBothShow()
    {
        var snap = Decode("""
        {"limits": [
          {"kind":"weekly_scoped","group":"weekly","percent":5,
           "scope":{"model":{"id":"claude-fable-5-1","display_name":"Fable"}}},
          {"kind":"weekly_scoped","group":"weekly","percent":9,
           "scope":{"model":{"id":"claude-opus-5-5","display_name":"Opus"}}}
        ]}
        """);
        Assert.Equal(2, snap.Scoped.Count);
        Assert.Equal(new HashSet<string> { "Fable weekly", "Opus weekly" }, snap.Scoped.Select(l => l.Name).ToHashSet());
    }

    [Fact]
    public void TwoScopedLimitsSameNameDistinctIdsBothShow()
    {
        var snap = Decode("""
        {"limits": [
          {"kind":"weekly_scoped","group":"weekly","percent":5,
           "scope":{"model":{"id":"id-a","display_name":"Fable"}}},
          {"kind":"weekly_scoped","group":"weekly","percent":9,
           "scope":{"model":{"id":"id-b","display_name":"Fable"}}}
        ]}
        """);
        Assert.Equal(2, snap.Scoped.Count);
    }

    [Fact]
    public void ParsesDateWithoutFraction()
    {
        Assert.Equal(DateTimeOffset.Parse("2026-03-04T15:00:00Z"), IsoDate.Parse("2026-03-04T15:00:00+00:00"));
        Assert.Null(IsoDate.Parse("not a date"));
    }

    [Fact]
    public void DecodesExtraUsage()
    {
        var e = Decode(Fixture).Extra!;
        Assert.False(e.IsEnabled);
        Assert.Equal(1250, e.UsedCredits);
        Assert.Equal(10000, e.MonthlyLimit);
        Assert.Equal(2, e.DecimalPlaces);
        Assert.Equal("Extra usage A$12.50 of A$100 · off (out of credits)", Formatters.ExtraUsageLine(e));
    }

    [Fact]
    public void FallsBackToFiveHourAndSevenDayWithoutLimits()
    {
        var snap = Decode("""
        {"five_hour": {"utilization": 12.5, "resets_at": "2026-03-02T08:20:00.1+00:00"},
         "seven_day": {"utilization": 40, "resets_at": "2026-03-04T15:00:00Z"}, "limits": null}
        """);
        Assert.Equal(new[] { "Session", "Weekly" }, snap.Ordered.Select(l => l.Name));
        Assert.Equal(12.5, snap.Session?.Percent);
        Assert.Equal(40, snap.Weekly?.Percent);
        Assert.NotNull(snap.Weekly?.ResetsAt);
    }

    [Fact]
    public void RejectsNonObject()
    {
        Assert.Throws<UsageDecoder.DecodeException>(() => Decode("[1,2]"));
        Assert.Throws<UsageDecoder.DecodeException>(() => Decode("not json"));
    }

    [Fact]
    public void ProfileDecode()
    {
        var p = Profile.Decode(Encoding.UTF8.GetBytes("""
        {"account": {"uuid": "a", "email": "x@example.com", "display_name": "X"},
         "organization": {"uuid": "o", "name": "Org", "organization_type": "claude_max", "rate_limit_tier": "t"}}
        """));
        Assert.Equal("x@example.com", p?.Email);
        Assert.Equal("claude_max", p?.OrganizationType);
        Assert.Equal("o", p?.OrganizationUuid);
        Assert.Null(Profile.Decode(Encoding.UTF8.GetBytes("[]")));
    }

    [Fact]
    public void RetryAfterSecondsAndHttpDate()
    {
        var now = DateTimeOffset.Parse("2026-10-25T08:00:00Z");
        Assert.Equal(120, UsageClient.RetryAfter("120", now));
        Assert.Equal(300, UsageClient.RetryAfter("Sun, 25 Oct 2026 08:05:00 GMT", now));
        Assert.Null(UsageClient.RetryAfter(null, now));
        Assert.Null(UsageClient.RetryAfter("soon", now));
    }

    [Fact]
    public void ClaudeCodeCredentialPayloadParse()
    {
        const string payload = """{"claudeAiOauth":{"accessToken":"tok-abc","refreshToken":"r","expiresAt":1790000000000,"subscriptionType":"max"},"mcpOAuth":{}}""";
        var t = ClaudeCodeCredentials.Parse(Encoding.UTF8.GetBytes(payload + "\n"));
        Assert.Equal("tok-abc", t.AccessToken);
        Assert.Equal(1_790_000_000, t.ExpiresAt!.Value.ToUnixTimeSeconds());
        Assert.Equal("max", t.SubscriptionType);
        Assert.Throws<CredentialException>(() => ClaudeCodeCredentials.Parse(Encoding.UTF8.GetBytes("{}")));
        // A UTF-8 byte order mark from a Windows editor must not break the parse.
        var bom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(payload)).ToArray();
        Assert.Equal("tok-abc", ClaudeCodeCredentials.Parse(bom).AccessToken);
    }

    [Fact]
    public void ClaudeCodeAccountInfoReadsOauthAccount()
    {
        var info = ClaudeCodeAccountInfo.Parse(Encoding.UTF8.GetBytes("""
        {"oauthAccount": {"emailAddress": "personal@example.com", "accountUuid": "a", "organizationUuid": "o",
                          "organizationName": "Org"}, "other": 1}
        """));
        Assert.Equal("personal@example.com", info?.Email);
        Assert.Equal("a", info?.AccountUuid);
        Assert.Equal("o", info?.OrganizationUuid);
        Assert.Null(ClaudeCodeAccountInfo.Parse(Encoding.UTF8.GetBytes("{}")));
    }

    [Fact]
    public void CliVersionPicksTheHighestInstalledVersion()
    {
        Assert.Equal("2.1.310", ClaudeCliVersion.Pick(new[] { "2.1.296", "2.1.310", "2.1.306", "notes" }));
        Assert.Equal("2.1.10", ClaudeCliVersion.Pick(new[] { "2.1.9", "2.1.10" }));
        Assert.Equal(ClaudeCliVersion.Fallback, ClaudeCliVersion.Pick(Array.Empty<string>()));
    }
}
