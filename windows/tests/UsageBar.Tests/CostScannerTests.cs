using System.Text.Json;
using System.Text.Json.Nodes;
using UsageBar.Core;

namespace UsageBar.Tests;

public sealed class CostScannerTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "usagebar-tests-" + Guid.NewGuid());
    readonly TimeZoneInfo utc = TimeZoneInfo.Utc;
    readonly DateTimeOffset now = DateTimeOffset.UtcNow;

    public CostScannerTests() => Directory.CreateDirectory(Path.Combine(dir, "proj", "sub"));
    public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { } }

    static string Iso(DateTimeOffset d) => d.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
    string TodayTs => Iso(now.AddSeconds(-60));
    string YesterdayTs => Iso(now.AddDays(-1));

    /// <summary>Builds one assistant log line.</summary>
    string Line(string? id, string? req, string session = "s1", string model = "claude-opus-5-5", string? ts = null,
                long input = 0, long output = 0, long? read = null, long? create = null, (long, long)? split = null,
                string? stop = "end_turn", string reqKey = "requestId")
    {
        var usage = new JsonObject { ["input_tokens"] = input, ["output_tokens"] = output };
        if (read is { } r) usage["cache_read_input_tokens"] = r;
        if (create is { } c) usage["cache_creation_input_tokens"] = c;
        if (split is { } s) usage["cache_creation"] = new JsonObject { ["ephemeral_5m_input_tokens"] = s.Item1, ["ephemeral_1h_input_tokens"] = s.Item2 };
        var msg = new JsonObject { ["model"] = model, ["usage"] = usage, ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "hi" }) };
        if (id is not null) msg["id"] = id;
        msg["stop_reason"] = stop;
        var obj = new JsonObject { ["type"] = "assistant", ["sessionId"] = session, ["timestamp"] = ts ?? TodayTs, ["message"] = msg };
        if (req is not null) obj[reqKey] = req;
        return obj.ToJsonString();
    }

    void Write(IEnumerable<string> lines, string name) =>
        File.WriteAllText(Path.Combine(dir, name), string.Join("\n", lines) + "\n");

    void Append(string text, string name) => File.AppendAllText(Path.Combine(dir, name), text);

    CostScanner Scanner() => new(dir, utc) { Workers = 2 };

    [Fact]
    public void StreamingLinesKeepTheLargestCumulativeUsage()
    {
        Write(new[]
        {
            Line("m1", "r1", input: 100, output: 1, read: 0, create: 0, stop: null),
            Line("m1", "r1", input: 100, output: 400, read: 0, create: 0),
            Line("m1", "r1", input: 100, output: 400, read: 0, create: 0),
        }, "proj/a.jsonl");
        var s = Scanner();
        s.Scan(now);
        Assert.Equal(500, s.Summary(now).TodayTokens);
    }

    [Fact]
    public void IncompleteRowNeverReplacesCompleteRow()
    {
        Write(new[] { Line("m1", "r1", input: 10, output: 5, read: 0, create: 0), Line("m1", "r1", input: 100, output: 0, stop: null) }, "proj/a.jsonl");
        var s = Scanner();
        s.Scan(now);
        Assert.Equal(15, s.Summary(now).TodayTokens);
    }

    [Fact]
    public void CompleteRowReplacesIncompleteRow()
    {
        Write(new[] { Line("m1", "r1", input: 100, output: 0, stop: null), Line("m1", "r1", input: 10, output: 5, read: 0, create: 0) }, "proj/a.jsonl");
        var s = Scanner();
        s.Scan(now);
        Assert.Equal(15, s.Summary(now).TodayTokens);
    }

    [Fact]
    public void CacheSplitIsUsedInsteadOfAggregate()
    {
        Write(new[] { Line("m1", "r1", read: 0, create: 1000, split: (600, 400)) }, "proj/a.jsonl");
        var s = Scanner();
        s.Scan(now);
        var sum = s.Summary(now);
        Assert.Equal(1000, sum.TodayTokens);
        Assert.Equal((600 * 5 + 400 * 8) / 1e6, sum.TodayUsd, 12);
    }

    [Fact]
    public void AggregateCacheCreationCountsAsFiveMinuteWhenSplitMissing()
    {
        Write(new[] { Line("m1", "r1", read: 0, create: 1000) }, "proj/a.jsonl");
        var s = Scanner();
        s.Scan(now);
        Assert.Equal(1000 * 5 / 1e6, s.Summary(now).TodayUsd, 12);
    }

    [Fact]
    public void PricingAcrossCategories()
    {
        var c = new TokenCounts(1_000_000, 1_000_000, 1_000_000, 1_000_000, 1_000_000);
        Assert.Equal(4 + 20 + 5 + 8 + 0.20, Pricing.Cost(c, "claude-opus-5-5")!.Value, 9);
        Assert.Equal(2 + 10 + 2.5 + 4 + 0.10, Pricing.Cost(c, "claude-sonnet-5-5")!.Value, 9);
        Assert.Equal(0.10 + 0.50 + 0.125 + 0.20 + 0.01, Pricing.Cost(c, "claude-haiku-5-5")!.Value, 9);
        Assert.Equal(10 + 50 + 12.5 + 20 + 0.25, Pricing.Cost(c, "claude-fable-5-1")!.Value, 9);
    }

    [Fact]
    public void UnknownModelsMapByFamilyOrStayUnpriced()
    {
        Assert.Equal(PriceMatch.Family("claude-opus-5-5"), Pricing.Match("claude-opus-5"));
        Assert.Equal(PriceMatch.Family("claude-haiku-5-5"), Pricing.Match("claude-haiku-4-5-20251001"));
        Assert.Equal(PriceMatch.Family("claude-fable-5-1"), Pricing.Match("fable"));
        Assert.Equal(PriceMatch.Exact("claude-opus-5-5"), Pricing.Match("claude-opus-5-5[1m]"));
        Assert.Equal(PriceMatch.Unpriced, Pricing.Match("lightning-128k"));
        Assert.Null(Pricing.Cost(new TokenCounts(Input: 5), "lightning-128k"));
    }

    [Fact]
    public void UnpricedModelCostsZeroAndIsListed()
    {
        Write(new[]
        {
            Line("m1", "r1", model: "lightning-128k", input: 1000, output: 10, read: 0, create: 0),
            Line("m2", "r2", model: "claude-sonnet-5-5", input: 1_000_000, output: 0, read: 0, create: 0),
        }, "proj/a.jsonl");
        var s = Scanner();
        s.Scan(now);
        var sum = s.Summary(now);
        Assert.Equal(new[] { "lightning-128k" }, sum.UnpricedModels);
        Assert.Equal(2.0, sum.TodayUsd, 9);
        Assert.Equal("claude-sonnet-5-5", sum.TopModel);
        Assert.Equal(1_001_010, sum.TodayTokens);
    }

    [Fact]
    public void FallbackKeyUsesSessionWhenRequestIdMissingAndAcceptsRequestIdAlias()
    {
        Write(new[]
        {
            Line("m1", null, session: "sA", input: 10, output: 1, read: 0, create: 0),
            Line("m1", null, session: "sB", input: 20, output: 1, read: 0, create: 0),
            Line("m2", "r9", input: 5, output: 1, read: 0, create: 0, reqKey: "request_id"),
            Line("m2", "r9", input: 5, output: 1, read: 0, create: 0),
        }, "proj/a.jsonl");
        var s = Scanner();
        s.Scan(now);
        Assert.Equal(11 + 21 + 6, s.Summary(now).TodayTokens);
    }

    [Fact]
    public void SkipsNonAssistantAndUnparseableLines()
    {
        const string user = """{"type":"user","message":{"content":"what is my \"usage\" today, assistant?"}}""";
        Write(new[] { user, "{not json \"usage\" \"assistant\"", "", Line("m1", "r1", input: 7, output: 3, read: 0, create: 0) }, "proj/a.jsonl");
        var s = Scanner();
        var stats = s.Scan(now);
        Assert.Equal(10, s.Summary(now).TodayTokens);
        Assert.Equal(1, stats.UniqueKeys);
    }

    [Fact]
    public void DuplicatesAcrossFilesCountOnceAndSubfoldersAreScanned()
    {
        var l = Line("m1", "r1", input: 50, output: 50, read: 0, create: 0);
        Write(new[] { l }, "proj/a.jsonl");
        Write(new[] { l }, "proj/sub/agent-1.jsonl");
        var s = Scanner();
        var stats = s.Scan(now);
        Assert.Equal(100, s.Summary(now).TodayTokens);
        Assert.Equal(1, stats.CrossFileDuplicateKeys);
        Assert.Equal(2, stats.FilesRead);
    }

    [Fact]
    public void IncrementalAppendReadsOnlyNewBytesAndWaitsForPartialLine()
    {
        var first = Line("m1", "r1", input: 10, output: 10, read: 0, create: 0);
        var second = Line("m2", "r2", input: 1, output: 1, read: 0, create: 0);
        var half = second[..(second.Length / 2)];
        Write(new[] { first }, "proj/a.jsonl");
        Append(half, "proj/a.jsonl");      // partial trailing line
        var s = Scanner();
        s.Scan(now);
        Assert.Equal(20, s.Summary(now).TodayTokens);

        Append(second[half.Length..] + "\n", "proj/a.jsonl");
        var stats = s.Scan(now);
        Assert.Equal(22, s.Summary(now).TodayTokens);
        Assert.True(stats.BytesRead < first.Length + second.Length);   // did not re-read the first line

        Assert.Equal(0, s.Scan(now).FilesRead);   // unchanged file: nothing read
    }

    [Fact]
    public void CrlfLineEndingsAreHandled()
    {
        File.WriteAllText(Path.Combine(dir, "proj", "crlf.jsonl"),
                          Line("m1", "r1", input: 3, output: 4, read: 0, create: 0) + "\r\n" + Line("m2", "r2", input: 1, output: 1, read: 0, create: 0) + "\r\n");
        var s = Scanner();
        s.Scan(now);
        Assert.Equal(9, s.Summary(now).TodayTokens);
    }

    [Fact]
    public void LaterLineInAppendSupersedesEarlierStreamingLine()
    {
        Write(new[] { Line("m1", "r1", input: 100, output: 1, read: 0, create: 0, stop: null) }, "proj/a.jsonl");
        var s = Scanner();
        s.Scan(now);
        Assert.Equal(101, s.Summary(now).TodayTokens);
        Append(Line("m1", "r1", input: 100, output: 900, read: 0, create: 0) + "\n", "proj/a.jsonl");
        s.Scan(now);
        Assert.Equal(1000, s.Summary(now).TodayTokens);
    }

    [Fact]
    public void DailyBucketsAndThirtyDayTotals()
    {
        Write(new[]
        {
            Line("m1", "r1", ts: TodayTs, input: 1_000_000, output: 0, read: 0, create: 0),
            Line("m2", "r2", ts: YesterdayTs, input: 2_000_000, output: 0, read: 0, create: 0),
            Line("m3", "r3", ts: Iso(now.AddDays(-40)), input: 9, output: 0, read: 0, create: 0),
        }, "proj/a.jsonl");
        var s = Scanner();
        s.Scan(now);
        var sum = s.Summary(now);
        Assert.Equal(4, sum.TodayUsd, 9);
        Assert.Equal(12, sum.Last30Usd, 9);
        Assert.Equal(3_000_000, sum.Last30Tokens);
        Assert.Equal(30, sum.DailyUsd.Length);
        Assert.Equal(4, sum.DailyUsd[29], 9);
        Assert.Equal(8, sum.DailyUsd[28], 9);
    }

    [Fact]
    public void CacheRoundTripKeepsTotalsAndAvoidsRescan()
    {
        Write(new[] { Line("m1", "r1", input: 10, output: 10, read: 5, create: 0, split: (3, 2)) }, "proj/a.jsonl");
        var s = Scanner();
        s.Scan(now);
        var encoded = JsonSerializer.Serialize(new CostCacheFile(CostCacheFile.CurrentVersion, new() { ["x"] = s.Export() }));
        // Counts only: no message content or ids in the cache.
        Assert.DoesNotContain("m1", encoded);
        Assert.DoesNotContain("\"hi\"", encoded);

        var decoded = JsonSerializer.Deserialize<CostCacheFile>(encoded)!;
        var s2 = Scanner();
        s2.Restore(decoded.Roots["x"]);
        Assert.Equal(s.Summary(now), s2.Summary(now));
        Assert.Equal(0, s2.Scan(now).FilesRead);
    }

    [Fact]
    public void FastTimestampParserMatchesIsoParser()
    {
        foreach (var t in new[] { "2026-10-25T07:41:12.345Z", "2026-01-01T00:00:00Z", "2024-02-29T23:59:59.999999Z" })
            Assert.InRange(CostScanner.FastEpoch(t)!.Value - IsoDate.Parse(t)!.Value.ToUnixTimeMilliseconds() / 1000.0, -0.001, 0.001);
        Assert.Null(CostScanner.FastEpoch("2026-10-25T07:41:12+10:00"));
    }
}
