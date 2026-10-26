using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace UsageBar.Core;

// MARK: Public results

public sealed record ModelCost(string Model, long Tokens, double Usd, PriceMatch Match);

public sealed class CostSummary : IEquatable<CostSummary>
{
    public double TodayUsd { get; set; }
    public long TodayTokens { get; set; }
    public double Last30Usd { get; set; }
    public long Last30Tokens { get; set; }
    /// <summary>30 daily costs in USD, oldest first, today last.</summary>
    public double[] DailyUsd { get; set; } = new double[30];
    /// <summary>30 daily token totals, oldest first, today last.</summary>
    public long[] DailyTokens { get; set; } = new long[30];
    public string? TopModel { get; set; }
    /// <summary>Models that contributed tokens but have no price (and so cost 0).</summary>
    public List<string> UnpricedModels { get; set; } = new();
    /// <summary>Per model over 30 days, highest cost first.</summary>
    public List<ModelCost> PerModel { get; set; } = new();

    public bool Equals(CostSummary? o) =>
        o is not null && TodayUsd == o.TodayUsd && TodayTokens == o.TodayTokens && Last30Usd == o.Last30Usd
        && Last30Tokens == o.Last30Tokens && DailyUsd.SequenceEqual(o.DailyUsd) && DailyTokens.SequenceEqual(o.DailyTokens)
        && TopModel == o.TopModel && UnpricedModels.SequenceEqual(o.UnpricedModels) && PerModel.SequenceEqual(o.PerModel);

    public override bool Equals(object? obj) => Equals(obj as CostSummary);
    public override int GetHashCode() => HashCode.Combine(TodayTokens, Last30Tokens, TopModel);
}

public sealed class ScanStats
{
    public int FilesFound { get; set; }
    public int FilesInWindow { get; set; }
    public int FilesRead { get; set; }
    public long BytesRead { get; set; }
    public int LinesDecoded { get; set; }
    public int RecordsMerged { get; set; }
    public int UniqueKeys { get; set; }
    /// <summary>Keys seen in more than one file during this pass (resumed or forked sessions).</summary>
    public int CrossFileDuplicateKeys { get; set; }
    public double Seconds { get; set; }
}

/// <summary>
/// One file's incremental position. No content is stored: <c>Offset</c> is the byte just after the last complete line,
/// so a partial trailing line is simply re-read on the next pass. <c>Identity</c> is the file's creation time, which
/// changes when a file is replaced (the Mac uses the inode).
/// </summary>
public sealed record FileState(long Size, double Mtime, ulong Identity, long Offset);

/// <summary>Persisted state for one logs root: token counts and id hashes only.</summary>
public sealed record RootCache(Dictionary<string, FileState> Files, List<string> Models, byte[] Records);

public sealed record CostCacheFile(int Version, Dictionary<string, RootCache> Roots)
{
    public const int CurrentVersion = 2;
}

internal readonly record struct ParsedRecord(ulong Key, int Day, string Model, TokenCounts Counts, bool Incomplete);

internal readonly record struct Entry(int Day, ushort Model, TokenCounts Counts, bool Incomplete);

/// <summary>Incremental scanner over <c>&lt;root&gt;/**/*.jsonl</c>. Not thread-safe: call from one thread at a time.</summary>
public sealed class CostScanner(string root, TimeZoneInfo timeZone)
{
    public string Root { get; } = root;
    public TimeZoneInfo TimeZone { get; set; } = timeZone;
    public int MaxAgeDays { get; set; } = 31;
    public int Workers { get; set; } = 6;

    Dictionary<string, FileState> files = new();
    List<string> models = new();
    Dictionary<string, ushort> modelIndex = new();
    Dictionary<ulong, Entry> entries = new();

    // MARK: Cache

    public void Restore(RootCache cache)
    {
        files = new Dictionary<string, FileState>(cache.Files);
        models = new List<string>(cache.Models);
        modelIndex = models.Select((m, i) => (m, i)).ToDictionary(x => x.m, x => (ushort)x.i);
        entries = Unpack(cache.Records, models.Count);
    }

    public RootCache Export() => new(new Dictionary<string, FileState>(files), new List<string>(models), Pack(entries));

    // Record layout, little-endian, 36 bytes: key u64, day i32, model u16, flags u16, 5 x u32 counts (as on the Mac).
    const int RecordSize = 36;

    static byte[] Pack(Dictionary<ulong, Entry> entries)
    {
        var data = new byte[entries.Count * RecordSize];
        var o = 0;
        static uint Clamp(long v) => (uint)Math.Clamp(v, 0, uint.MaxValue);
        foreach (var (key, e) in entries)
        {
            var s = data.AsSpan(o, RecordSize);
            BinaryPrimitives.WriteUInt64LittleEndian(s, key);
            BinaryPrimitives.WriteInt32LittleEndian(s[8..], e.Day);
            BinaryPrimitives.WriteUInt16LittleEndian(s[12..], e.Model);
            BinaryPrimitives.WriteUInt16LittleEndian(s[14..], (ushort)(e.Incomplete ? 1 : 0));
            BinaryPrimitives.WriteUInt32LittleEndian(s[16..], Clamp(e.Counts.Input));
            BinaryPrimitives.WriteUInt32LittleEndian(s[20..], Clamp(e.Counts.Output));
            BinaryPrimitives.WriteUInt32LittleEndian(s[24..], Clamp(e.Counts.CacheWrite5m));
            BinaryPrimitives.WriteUInt32LittleEndian(s[28..], Clamp(e.Counts.CacheWrite1h));
            BinaryPrimitives.WriteUInt32LittleEndian(s[32..], Clamp(e.Counts.CacheRead));
            o += RecordSize;
        }
        return data;
    }

    static Dictionary<ulong, Entry> Unpack(byte[] data, int modelCount)
    {
        var n = data.Length / RecordSize;
        var out_ = new Dictionary<ulong, Entry>(n);
        for (var i = 0; i < n; i++)
        {
            var s = data.AsSpan(i * RecordSize, RecordSize);
            var model = BinaryPrimitives.ReadUInt16LittleEndian(s[12..]);
            if (model >= modelCount) continue;
            var c = new TokenCounts(BinaryPrimitives.ReadUInt32LittleEndian(s[16..]), BinaryPrimitives.ReadUInt32LittleEndian(s[20..]),
                                    BinaryPrimitives.ReadUInt32LittleEndian(s[24..]), BinaryPrimitives.ReadUInt32LittleEndian(s[28..]),
                                    BinaryPrimitives.ReadUInt32LittleEndian(s[32..]));
            out_[BinaryPrimitives.ReadUInt64LittleEndian(s)] =
                new Entry(BinaryPrimitives.ReadInt32LittleEndian(s[8..]), model, c, (BinaryPrimitives.ReadUInt16LittleEndian(s[14..]) & 1) != 0);
        }
        return out_;
    }

    // MARK: Scan

    sealed record Job(string Path, long From, FileState State);

    sealed class JobResult
    {
        public List<ParsedRecord> Records { get; } = new();
        public long Consumed { get; set; }
        public long Bytes { get; set; }
        public int Lines { get; set; }
        public bool Ok { get; set; } = true;
    }

    static readonly EnumerationOptions Walk = new()
    {
        RecurseSubdirectories = true, IgnoreInaccessible = true, MatchType = MatchType.Simple,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
    };

    /// <summary>Reads new bytes from every recently modified log and merges them. Returns counts only.</summary>
    public ScanStats Scan(DateTimeOffset now)
    {
        var started = Stopwatch.StartNew();
        var stats = new ScanStats();
        var cutoff = now.ToUnixTimeMilliseconds() / 1000.0 - MaxAgeDays * 86400.0;

        var jobs = new List<Job>();
        var seen = new HashSet<string>();
        IEnumerable<string> found;
        try { found = Directory.Exists(Root) ? Directory.EnumerateFiles(Root, "*.jsonl", Walk) : Enumerable.Empty<string>(); }
        catch (IOException) { found = Enumerable.Empty<string>(); }
        foreach (var path in found)
        {
            if (!path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) continue;
            if (System.IO.Path.GetRelativePath(Root, path).Split('\\', '/').Any(seg => seg.StartsWith('.'))) continue;
            stats.FilesFound++;
            FileInfo fi;
            try { fi = new FileInfo(path); if (!fi.Exists) continue; }
            catch (IOException) { continue; }
            var mtime = (fi.LastWriteTimeUtc - DateTime.UnixEpoch).TotalSeconds;
            if (mtime < cutoff) continue;
            stats.FilesInWindow++;
            seen.Add(path);
            var current = new FileState(fi.Length, mtime, (ulong)fi.CreationTimeUtc.Ticks, 0);
            if (files.TryGetValue(path, out var old))
            {
                if (old.Identity == current.Identity && old.Size == current.Size && old.Mtime == current.Mtime) continue;
                // Same file grown: read only the new bytes. Shrunk or replaced: rescan from the start
                // (de-dup keeps the totals right for records already merged).
                var from = old.Identity == current.Identity && current.Size >= old.Offset ? old.Offset : 0;
                jobs.Add(new Job(path, from, current));
            }
            else jobs.Add(new Job(path, 0, current));
        }
        // Forget files that were deleted or fell out of the window.
        foreach (var key in files.Keys.Where(k => !seen.Contains(k)).ToList()) files.Remove(key);

        jobs.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        var results = new JobResult[jobs.Count];
        var tz = TimeZone;
        Parallel.For(0, jobs.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Workers) },
                     i => results[i] = ParseFile(jobs[i].Path, jobs[i].From, tz));

        var firstFile = new Dictionary<ulong, int>();
        var dupKeys = new HashSet<ulong>();
        for (var i = 0; i < jobs.Count; i++)
        {
            var r = results[i];
            if (!r.Ok) continue;
            stats.FilesRead++;
            stats.BytesRead += r.Bytes;
            stats.LinesDecoded += r.Lines;
            foreach (var rec in r.Records)
            {
                if (firstFile.TryGetValue(rec.Key, out var f)) { if (f != i) dupKeys.Add(rec.Key); }
                else firstFile[rec.Key] = i;
                Merge(rec);
                stats.RecordsMerged++;
            }
            files[jobs[i].Path] = jobs[i].State with { Offset = jobs[i].From + r.Consumed };
        }
        Prune(now);
        stats.CrossFileDuplicateKeys = dupKeys.Count;
        stats.UniqueKeys = entries.Count;
        stats.Seconds = started.Elapsed.TotalSeconds;
        return stats;
    }

    /// <summary>
    /// Merge rule: never let an incomplete row replace a complete one; otherwise the larger total wins, ties go to the
    /// later line.
    /// </summary>
    void Merge(ParsedRecord r)
    {
        if (!modelIndex.TryGetValue(r.Model, out var mi))
        {
            mi = (ushort)Math.Min(models.Count, ushort.MaxValue);
            models.Add(r.Model);
            modelIndex[r.Model] = mi;
        }
        var next = new Entry(r.Day, mi, r.Counts, r.Incomplete);
        if (!entries.TryGetValue(r.Key, out var old)) { entries[r.Key] = next; return; }
        if (!old.Incomplete && r.Incomplete) return;
        if ((old.Incomplete && !r.Incomplete) || r.Counts.Total >= old.Counts.Total) entries[r.Key] = next;
    }

    void Prune(DateTimeOffset now)
    {
        var oldest = DayNumber(now, TimeZone) - MaxAgeDays - 1;
        entries = entries.Where(e => e.Value.Day >= oldest).ToDictionary(e => e.Key, e => e.Value);
    }

    // MARK: Summary

    public CostSummary Summary(DateTimeOffset now)
    {
        var today = DayNumber(now, TimeZone);
        var first = today - 29;
        // day offset (0...29) x model index -> counts
        var buckets = new Dictionary<int, Dictionary<ushort, TokenCounts>>();
        foreach (var e in entries.Values)
        {
            if (e.Day < first || e.Day > today) continue;
            var day = buckets.TryGetValue(e.Day - first, out var d) ? d : buckets[e.Day - first] = new();
            day[e.Model] = (day.TryGetValue(e.Model, out var c) ? c : default) + e.Counts;
        }

        var s = new CostSummary();
        var perModel = new Dictionary<ushort, (long Tokens, double Usd)>();
        // Sorted, so the floating-point sums come out the same on every run and on every platform.
        foreach (var dayOffset in buckets.Keys.OrderBy(k => k))
        {
            var byModel = buckets[dayOffset];
            foreach (var mi in byModel.Keys.OrderBy(k => k))
            {
                var counts = byModel[mi];
                var name = mi < models.Count ? models[mi] : "unknown";
                var usd = Pricing.Cost(counts, name) ?? 0;
                s.DailyUsd[dayOffset] += usd;
                s.DailyTokens[dayOffset] += counts.Total;
                s.Last30Usd += usd;
                s.Last30Tokens += counts.Total;
                if (dayOffset == 29)
                {
                    s.TodayUsd += usd;
                    s.TodayTokens += counts.Total;
                }
                var pm = perModel.TryGetValue(mi, out var v) ? v : (0, 0.0);
                perModel[mi] = (pm.Tokens + counts.Total, pm.Usd + usd);
            }
        }
        s.PerModel = perModel.OrderBy(p => p.Key)
            .Select(p => { var name = p.Key < models.Count ? models[p.Key] : "unknown"; return new ModelCost(name, p.Value.Tokens, p.Value.Usd, Pricing.Match(name)); })
            .OrderByDescending(m => m.Usd).ThenByDescending(m => m.Tokens).ToList();
        s.TopModel = s.PerModel.FirstOrDefault(m => m.Usd > 0)?.Model;
        s.UnpricedModels = s.PerModel.Where(m => m.Match.Kind == PriceMatchKind.Unpriced && m.Tokens > 0)
                                     .Select(m => m.Model).OrderBy(m => m, StringComparer.Ordinal).ToList();
        return s;
    }

    // MARK: Parsing

    static readonly byte[] UsageNeedle = "\"usage\""u8.ToArray();
    static readonly byte[] AssistantNeedle = "\"assistant\""u8.ToArray();

    static JobResult ParseFile(string path, long from, TimeZoneInfo tz)
    {
        var result = new JobResult();
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            fs.Seek(Math.Max(0, from), SeekOrigin.Begin);
            var buffer = new byte[4 << 20];
            var pending = 0;              // bytes in buffer not yet consumed
            var pendingStart = from;      // file offset of buffer[0]
            while (true)
            {
                if (pending == buffer.Length) Array.Resize(ref buffer, buffer.Length * 2);   // one very long line
                var read = fs.Read(buffer, pending, buffer.Length - pending);
                if (read <= 0) break;
                result.Bytes += read;
                var count = pending + read;
                var start = 0;
                while (start < count)
                {
                    var nl = Array.IndexOf(buffer, (byte)'\n', start, count - start);
                    if (nl < 0) break;
                    if (nl > start)
                    {
                        if (ParseLine(buffer.AsSpan(start, nl - start), path, pendingStart + start, tz) is { } rec) result.Records.Add(rec);
                        result.Lines++;
                    }
                    start = nl + 1;
                }
                // Keep the partial trailing line for the next read.
                Buffer.BlockCopy(buffer, start, buffer, 0, count - start);
                pending = count - start;
                pendingStart += start;
            }
            result.Consumed = pendingStart - from;
        }
        catch (IOException) { result.Ok = false; }
        catch (UnauthorizedAccessException) { result.Ok = false; }
        return result;
    }

    internal static ParsedRecord? ParseLine(ReadOnlySpan<byte> line, string path, long lineOffset, TimeZoneInfo tz)
    {
        // Cheap pre-filters before any JSON work.
        if (line.IndexOf(UsageNeedle) < 0 || line.IndexOf(AssistantNeedle) < 0) return null;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line.ToArray()); }
        catch (JsonException) { return null; }
        using (doc)
        {
            var o = doc.RootElement;
            if (o.ValueKind != JsonValueKind.Object || Json.String(o, "type") != "assistant") return null;
            if (Json.Object(o, "message") is not { } msg || Json.Object(msg, "usage") is not { } u) return null;

            long N(JsonElement? e, string k) => Json.Number(e, k) is { } d ? (long)d : 0;
            var cacheCreation = Json.Object(u, "cache_creation");
            var input = N(u, "input_tokens");
            var output = N(u, "output_tokens");
            var cacheRead = N(u, "cache_read_input_tokens");
            long w5, w1;
            // Use the 5m/1h split when present; only fall back to the aggregate (as 5m) when it is missing.
            if (cacheCreation is { } cc && (Json.Number(cc, "ephemeral_5m_input_tokens") is not null || Json.Number(cc, "ephemeral_1h_input_tokens") is not null))
            {
                w5 = N(cc, "ephemeral_5m_input_tokens");
                w1 = N(cc, "ephemeral_1h_input_tokens");
            }
            else
            {
                w5 = N(u, "cache_creation_input_tokens");
                w1 = 0;
            }
            var counts = new TokenCounts(input, output, w5, w1, cacheRead);
            if (counts.Total <= 0) return null;

            var incomplete = Json.String(msg, "stop_reason") is null && input > 0 && output == 0
                && Json.Number(u, "cache_read_input_tokens") is null && Json.Number(u, "cache_creation_input_tokens") is null
                && cacheCreation is null;

            var mid = Json.String(msg, "id");
            var rid = Json.String(o, "requestId") ?? Json.String(o, "request_id");
            var sid = Json.String(o, "sessionId");
            var keyString = mid is not null && rid is not null ? mid + ":" + rid
                : mid is not null && sid is not null ? sid + ":" + mid
                : mid ?? path + "#" + lineOffset;   // no id at all: unique per line, stable across rescans

            if (Json.String(o, "timestamp") is not { } ts) return null;
            double? epoch = FastEpoch(ts) ?? (IsoDate.Parse(ts) is { } d ? d.ToUnixTimeMilliseconds() / 1000.0 : null);
            if (epoch is null) return null;
            return new ParsedRecord(Fnv1a(keyString), DayNumber(epoch.Value, tz), Json.String(msg, "model") ?? "unknown", counts, incomplete);
        }
    }

    // MARK: Helpers

    public static int DayNumber(DateTimeOffset date, TimeZoneInfo tz) => DayNumber(date.ToUnixTimeMilliseconds() / 1000.0, tz);

    static int DayNumber(double epoch, TimeZoneInfo tz)
    {
        var at = DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Floor(epoch * 1000));
        var local = epoch + tz.GetUtcOffset(at).TotalSeconds;
        return (int)Math.Floor(local / 86400);
    }

    internal static ulong Fnv1a(string s)
    {
        ulong h = 0xcbf29ce484222325;
        foreach (var b in Encoding.UTF8.GetBytes(s))
        {
            h ^= b;
            h = unchecked(h * 0x100000001b3);
        }
        return h;
    }

    /// <summary>Fast path for "YYYY-MM-DDTHH:MM:SS[.fff]Z"; null for anything else.</summary>
    public static double? FastEpoch(string s)
    {
        var b = s;
        if (b.Length < 20 || b[4] != '-' || b[7] != '-' || b[10] != 'T' || b[13] != ':' || b[16] != ':' || b[^1] != 'Z') return null;
        int? Num(int from, int n)
        {
            var v = 0;
            for (var i = from; i < from + n; i++)
            {
                var c = b[i] - '0';
                if (c is < 0 or > 9) return null;
                v = v * 10 + c;
            }
            return v;
        }
        if (Num(0, 4) is not { } y || Num(5, 2) is not { } mo || Num(8, 2) is not { } d || Num(11, 2) is not { } h
            || Num(14, 2) is not { } mi || Num(17, 2) is not { } sec) return null;
        var frac = 0.0;
        if (b.Length > 20 && b[19] == '.')
        {
            var scale = 0.1;
            for (var i = 20; i < b.Length - 1; i++)
            {
                var c = b[i] - '0';
                if (c is < 0 or > 9) return null;
                frac += c * scale;
                scale /= 10;
            }
        }
        else if (b.Length != 20) return null;
        // Days from civil (Howard Hinnant).
        long yy = mo <= 2 ? y - 1 : y;
        var era = (yy >= 0 ? yy : yy - 399) / 400;
        var yoe = yy - era * 400;
        var mp = (mo + 9) % 12;
        var doy = (153 * mp + 2) / 5 + d - 1;
        var doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
        var days = era * 146097 + doe - 719468;
        return days * 86400 + h * 3600 + mi * 60 + sec + frac;
    }
}
