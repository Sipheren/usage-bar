using System.IO;
using System.Text.Json;
using UsageBar.Core;

namespace UsageBar;

/// <summary>
/// Runs the cost scanner off the UI thread, one scanner per logs folder, and keeps its incremental cache (counts and id
/// hashes only) in %LOCALAPPDATA%\UsageBar so a relaunch doesn't re-read every log.
/// </summary>
internal sealed class CostService(string cachePath)
{
    readonly Dictionary<string, CostScanner> scanners = new(StringComparer.OrdinalIgnoreCase);
    readonly SemaphoreSlim gate = new(1, 1);
    CostCacheFile? cache;

    public async Task<CostSummary> SummaryAsync(string root, DateTimeOffset now)
    {
        await gate.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                if (!scanners.TryGetValue(root, out var scanner))
                {
                    scanner = new CostScanner(root, TimeZoneInfo.Local);
                    if (LoadCache().Roots.TryGetValue(root, out var saved)) scanner.Restore(saved);
                    scanners[root] = scanner;
                }
                var stats = scanner.Scan(now);
                Log.Info($"cost: {stats.FilesRead} of {stats.FilesInWindow} files read, {stats.RecordsMerged} records, {stats.UniqueKeys} unique, {stats.Seconds:0.00}s");
                SaveCache(root, scanner.Export());
                return scanner.Summary(now);
            });
        }
        finally { gate.Release(); }
    }

    CostCacheFile LoadCache()
    {
        if (cache is not null) return cache;
        try
        {
            if (File.Exists(cachePath) && JsonSerializer.Deserialize<CostCacheFile>(File.ReadAllText(cachePath)) is { Version: CostCacheFile.CurrentVersion } c)
                return cache = c;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        return cache = new CostCacheFile(CostCacheFile.CurrentVersion, new());
    }

    void SaveCache(string root, RootCache rc)
    {
        var c = LoadCache();
        c.Roots[root] = rc;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            File.WriteAllText(cachePath + ".tmp", JsonSerializer.Serialize(c));
            File.Move(cachePath + ".tmp", cachePath, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
