using System.Globalization;
using System.Text.Json;
using UsageBar.Core;

namespace UsageBar.Tests;

/// <summary>Runs only when USAGEBAR_FIXTURES points at a fixtures folder (local test data, never in the repository).</summary>
public sealed class FixtureFactAttribute : FactAttribute
{
    public FixtureFactAttribute()
    {
        if (string.IsNullOrEmpty(FixtureParityTests.Dir)) Skip = "USAGEBAR_FIXTURES is not set";
    }
}

/// <summary>
/// Reproduces the macOS app's answer sheet (expected.json) from the same fixture files: strings exactly, numbers to the
/// decimals written, money within 0.000001.
/// </summary>
public class FixtureParityTests
{
    public static readonly string? Dir = Environment.GetEnvironmentVariable("USAGEBAR_FIXTURES");

    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-03-02T05:00:00Z");
    static readonly TimeZoneInfo Tz = TimeZoneInfo.FindSystemTimeZoneById("Australia/Brisbane");
    const double AudPerUsd = 1.5;

    static byte[] File_(string name) => File.ReadAllBytes(Path.Combine(Dir!, name));
    static JsonElement Expected()
    {
        using var doc = JsonDocument.Parse(File_("expected.json"));
        return doc.RootElement.Clone();
    }

    static string? Str(JsonElement e, string key) => e.GetProperty(key).ValueKind == JsonValueKind.Null ? null : e.GetProperty(key).GetString();

    /// <summary>Compares to the precision the sheet wrote the number with.</summary>
    static void Num(JsonElement e, string key, double? actual, double? tolerance = null)
    {
        var v = e.GetProperty(key);
        if (v.ValueKind == JsonValueKind.Null) { Assert.Null(actual); return; }
        Assert.NotNull(actual);
        var raw = v.GetRawText();
        var decimals = raw.Contains('.') ? raw.Length - raw.IndexOf('.') - 1 : 0;
        var tol = tolerance ?? 0.5 * Math.Pow(10, -decimals) + 1e-12;
        Assert.True(Math.Abs(v.GetDouble() - actual!.Value) <= tol, $"{key}: expected {raw}, got {actual.Value.ToString(CultureInfo.InvariantCulture)}");
    }

    static string Iso(DateTimeOffset d) => d.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    [FixtureFact]
    public void Profile_()
    {
        var e = Expected().GetProperty("profile");
        var p = Profile.Decode(File_("api/oauth-profile.json"))!;
        Assert.Equal(Str(e, "email"), p.Email);
        Assert.Equal(Str(e, "displayName"), p.DisplayName);
        Assert.Equal(Str(e, "accountUuid"), p.AccountUuid);
        Assert.Equal(Str(e, "organizationUuid"), p.OrganizationUuid);
        Assert.Equal(Str(e, "organizationName"), p.OrganizationName);
        Assert.Equal(Str(e, "organizationType"), p.OrganizationType);
        Assert.Equal(Str(e, "rateLimitTier"), p.RateLimitTier);
    }

    static void CheckAccount(JsonElement e, UsageSnapshot snap)
    {
        var limits = e.GetProperty("limits").EnumerateArray().ToList();
        Assert.Equal(limits.Count, snap.Ordered.Count);
        foreach (var (x, l) in limits.Zip(snap.Ordered))
        {
            var d = new LimitDisplay(l, Now, Tz);
            var pace = l.ResetsAt is { } r ? Pace.Compute(l.Percent, r, l.WindowMinutes, Now) : null;
            Assert.Equal(Str(x, "name"), l.Name);
            Num(x, "percent", l.Percent);
            Assert.Equal(Str(x, "percentText"), d.PercentText);
            Assert.Equal(Str(x, "resetsAt"), l.ResetsAt is { } ra ? Iso(ra) : null);
            Assert.Equal(Str(x, "resetText"), d.CompactTrailing);
            Assert.Equal(Str(x, "resetTextLarge"), d.LargeTrailing);
            Assert.Equal(Str(x, "paceText"), d.PaceLine);
            Assert.Equal(Str(x, "paceStatus"), pace?.Status switch
            {
                PaceStatus.OnPace => "onPace", PaceStatus.Under => "under", PaceStatus.Over => "over", _ => null,
            });
            Num(x, "expectedPercent", pace?.Expected);
            Num(x, "tickFraction", d.TickFraction);
            Assert.Equal(Str(x, "runOutRounded"), pace?.RunOut is { } ro ? Iso(Formatters.RoundTo(ro, 600)) : null);
            Assert.Equal(Str(x, "runOutText"), pace?.RunOut is { } rt ? Formatters.RunOutTime(rt, Now, Tz) : null);
        }
        Assert.Equal(Str(e, "extraUsage"), snap.Extra is { } ex ? Formatters.ExtraUsageLine(ex) : null);
    }

    [FixtureFact]
    public void PersonalAccount()
    {
        var e = Expected().GetProperty("accounts")[0];
        Assert.Equal("P", Str(e, "tag"));
        CheckAccount(e, UsageDecoder.Decode(File_("api/oauth-usage.json"), Now));
    }

    [FixtureFact]
    public void WorkAccountAndOrganizationChoice()
    {
        var e = Expected().GetProperty("accounts")[1];
        Assert.Equal("W", Str(e, "tag"));
        CheckAccount(e, UsageDecoder.Decode(File_("api/claude-ai-org-usage.json"), Now));

        var orgs = ClaudeAiCookieClient.ParseOrganizations(File_("api/claude-ai-organizations.json"));
        var personalOrg = Profile.Decode(File_("api/oauth-profile.json"))!.OrganizationUuid;
        var o = e.GetProperty("organizations");
        Assert.Equal(o.GetProperty("count").GetInt32(), orgs.Count);
        Assert.Equal(Str(o, "personalOrg"), personalOrg);
        var picked = ClaudeAiCookieClient.PickOrg(orgs, null, personalOrg)!;
        var x = o.GetProperty("pickedWithoutLastActiveOrg");
        Assert.Equal(Str(x, "uuid"), picked.Uuid);
        Assert.Equal(Str(x, "name"), picked.Name);
        Assert.Equal(x.GetProperty("isPersonal").GetBoolean(), ClaudeAiCookieClient.IsPersonalOrg(picked, personalOrg));
    }

    [FixtureFact]
    public void TrayLabelPlainText()
    {
        var e = Expected().GetProperty("menuBar");
        var inputs = new List<TrayInput>
        {
            new("P", "Personal", UsageDecoder.Decode(File_("api/oauth-usage.json"), Now), Now),
            new("W", "Work", UsageDecoder.Decode(File_("api/claude-ai-org-usage.json"), Now), Now),
        };
        string Text(LabelStyle s, MetricSelection m) => TrayModel.Build(inputs, s, m, Now).PlainText;
        var all = new MetricSelection(true, true, true);
        Assert.Equal(Str(e, "bars"), Text(LabelStyle.Bars, new()));
        Assert.Equal(Str(e, "percentages"), Text(LabelStyle.Percentages, new()));
        Assert.Equal(Str(e, "compact"), Text(LabelStyle.Compact, new()));
        Assert.Equal(Str(e, "iconOnly"), Text(LabelStyle.IconOnly, new()));
        Assert.Equal(Str(e, "barsWithModelWeekly"), Text(LabelStyle.Bars, all));
        Assert.Equal(Str(e, "percentagesWithModelWeekly"), Text(LabelStyle.Percentages, all));
    }

    [FixtureFact]
    public void CostFromTheSyntheticLogs()
    {
        var e = Expected().GetProperty("cost");
        var scanner = new CostScanner(Path.Combine(Dir!, "claude-code-logs", "projects"), Tz);
        var stats = scanner.Scan(Now);
        var s = scanner.Summary(Now);
        var rates = new ExchangeRates(new Dictionary<string, double> { ["USD"] = 1, ["AUD"] = AudPerUsd }, Now, Now);

        var dd = e.GetProperty("dedup");
        Assert.Equal(dd.GetProperty("jsonlFiles").GetInt32(), stats.FilesRead);
        Assert.Equal(dd.GetProperty("linesRead").GetInt32(), stats.LinesDecoded);
        Assert.Equal(dd.GetProperty("usageRecords").GetInt32(), stats.RecordsMerged);
        Assert.Equal(dd.GetProperty("uniqueMessages").GetInt32(), stats.UniqueKeys);
        Assert.Equal(dd.GetProperty("duplicatesCollapsed").GetInt32(), stats.RecordsMerged - stats.UniqueKeys);
        Assert.Equal(dd.GetProperty("crossFileDuplicateKeys").GetInt32(), stats.CrossFileDuplicateKeys);

        void Money(JsonElement x, double usd)
        {
            var aud = Currency.Convert(usd, "AUD", rates)!.Value;
            Num(x, "usd", usd, 0.000001 + 1e-12);
            Num(x, "aud", aud, 0.000001 + 1e-12);
            Assert.Equal(Str(x, "usdText"), Currency.Format(usd, "USD"));
            Assert.Equal(Str(x, "audText"), Currency.Format(aud, "AUD"));
        }

        var days = e.GetProperty("days").EnumerateArray().ToList();
        var nonEmpty = Enumerable.Range(0, 30).Where(i => s.DailyTokens[i] > 0 || s.DailyUsd[i] > 0).ToList();
        Assert.Equal(days.Count, nonEmpty.Count);
        foreach (var (x, offset) in days.Zip(nonEmpty))
        {
            var date = TimeZoneInfo.ConvertTime(Now, Tz).AddDays(offset - 29).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            Assert.Equal(Str(x, "date"), date);
            Money(x, s.DailyUsd[offset]);
            Assert.Equal(x.GetProperty("tokens").GetInt64(), s.DailyTokens[offset]);
        }

        var today = e.GetProperty("today");
        Money(today, s.TodayUsd);
        Assert.Equal(today.GetProperty("tokens").GetInt64(), s.TodayTokens);
        Assert.Equal(Str(today, "tokensText"), Formatters.Tokens(s.TodayTokens));
        var last30 = e.GetProperty("last30Days");
        Money(last30, s.Last30Usd);
        Assert.Equal(last30.GetProperty("tokens").GetInt64(), s.Last30Tokens);
        Assert.Equal(Str(last30, "tokensText"), Formatters.Tokens(s.Last30Tokens));

        var models = e.GetProperty("byModel").EnumerateArray().ToList();
        Assert.Equal(models.Count, s.PerModel.Count);
        foreach (var (x, m) in models.Zip(s.PerModel))
        {
            Assert.Equal(Str(x, "model"), m.Model);
            Assert.Equal(x.GetProperty("tokens").GetInt64(), m.Tokens);
            Money(x, m.Usd);
            Assert.Equal(Str(x, "pricing"), m.Match.Kind switch
            {
                PriceMatchKind.Exact => "exact:" + m.Match.Key, PriceMatchKind.Family => "family:" + m.Match.Key, _ => "unpriced",
            });
        }
        Assert.Equal(Str(e, "topModel"), s.TopModel);
        Assert.Equal(e.GetProperty("unpricedModels").EnumerateArray().Select(x => x.GetString()), s.UnpricedModels);
    }

    [FixtureFact]
    public void ErrorHandling()
    {
        var e = Expected().GetProperty("errors");
        var ra = e.GetProperty("retryAfterSeconds");
        Num(ra, "120", UsageClient.RetryAfter("120", Now));
        Num(ra, "Mon, 02 Mar 2026 05:02:00 GMT", UsageClient.RetryAfter("Mon, 02 Mar 2026 05:02:00 GMT", Now));

        var until = Now.AddSeconds(120);
        Assert.Equal(Str(e, "rateLimitedWithNumbers"), new AccountPresentation(AccountIssue.RateLimited(until), true, timeZone: Tz).HeaderNote);
        Assert.Equal(Str(e, "rateLimitedWithoutNumbers"), AccountIssue.RateLimited(until).Message(Tz));

        var e401 = e.GetProperty("error401");
        Assert.Equal(e401.GetProperty("isScopeError").GetBoolean(), UsageClient.IsScopeError(File_("api/error-401.json")));
        Assert.Equal(Str(e401, "claudeCodeMessage"), AccountFetcher.ClaudeCodeRejected.Message(Tz));

        var e403 = e.GetProperty("error403Scope");
        Assert.Equal(e403.GetProperty("isScopeError").GetBoolean(), UsageClient.IsScopeError(File_("api/error-403-scope.json")));
        Assert.Equal(Str(e403, "message"), AccountIssue.Scope.Message(Tz));

        var cf = e.GetProperty("cloudflare");
        var html = File_("api/cloudflare-challenge.html");
        Assert.Equal(cf.GetProperty("status503Body").GetBoolean(), ClaudeAiCookieClient.IsCloudflareChallenge(503, null, html));
        Assert.Equal(cf.GetProperty("cfMitigatedHeader").GetBoolean(), ClaudeAiCookieClient.IsCloudflareChallenge(200, "challenge", Array.Empty<byte>()));
        Assert.Equal(cf.GetProperty("status200Body").GetBoolean(), ClaudeAiCookieClient.IsCloudflareChallenge(200, null, html));
        // The Mac names Safari; the shared message takes the place to open as a parameter.
        Assert.Equal(Str(cf, "message"), AccountIssue.Cloudflare("Safari").Message(Tz));
    }
}
