using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using UsageBar.Core;

namespace UsageBar;

/// <summary>
/// The Claude app's existing sign-in, read-only. Electron's safeStorage on Windows protects the token cache with an
/// AES-256-GCM key that DPAPI unwraps for the same Windows user. Nothing is read until the user clicks Connect…
/// (decision 12); the only earlier read is config.json, to see whether the app is signed in.
/// </summary>
internal sealed class DesktopSource(HttpClient http) : IDesktopFetcher
{
    static string Dir => Paths.ClaudeDesktop;
    readonly AccountFetcher api = new(http);
    readonly ClaudeAiCookieClient cookies = new(http);
    byte[]? key;

    public async Task<FetchReport> FetchAsync(DesktopContext ctx, DateTimeOffset now, CancellationToken ct)
    {
        var report = new FetchReport { Email = ctx.KnownEmail };
        DesktopConfig config;
        try { config = DesktopConfig.Parse(Paths.ReadShared(Path.Combine(Dir, "config.json"))); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CredentialException)
        {
            report.Issue = AccountIssue.NotSignedIn("Claude app not found or not signed in");
            return report;
        }
        if (config.EncryptedCaches.Count == 0)
        {
            report.Issue = AccountIssue.NotSignedIn("Not signed in to the Claude app");
            return report;
        }
        if (!ctx.Connected)
        {
            report.Issue = AccountIssue.NotConnected;
            return report;
        }

        // Cookie first: claude.ai's usage endpoint has its own allowance, while the token route's OAuth endpoint allows
        // only a few requests, shared with Claude Code (decision 18). The cookie is read once per fetch.
        AccountIssue? cookieIssue = null;
        string? sessionKey;
        try { sessionKey = SessionKeyCookie(); }
        catch (CredentialException e) { sessionKey = null; Log.Info("desktop: cookie " + e.Error); }
        if (sessionKey is not null)
        {
            report.Route = "cookie";
            var outcome = await AccountFetcher.ApplyCookieFlowAsync(cookies, new Dictionary<string, string> { ["sessionKey"] = sessionKey },
                                                                    report, AccountIssue.ExpiredDesktop, "the Claude app", now, ct);
            if (AccountFetcher.DesktopCookieResultStands(outcome.Kind)) return report;
            Log.Info($"desktop: cookie {report.Issue?.LogName ?? "noData"}, trying the token");
            cookieIssue = report.Issue;
            report.Issue = null;
        }

        var candidates = new List<TokenCandidate>();
        try { candidates = Candidates(config); }
        catch (CredentialException e) { Log.Info("desktop: token cache " + e.Error); }

        var accountUuid = config.LastKnownAccountUuid;
        var cachedPath = accountUuid is not null && ctx.CachedChoice.TryGetValue(accountUuid, out var p) ? p : null;
        var ordered = DesktopTokenParser.Ordered(candidates, accountUuid, cachedPath, now);
        Log.Info($"desktop: {candidates.Count} token candidates");

        TokenCandidate? chosen;
        // A candidate tied to the account uuid, or an unexpired cached path; otherwise ask each token's profile.
        if (ordered.FirstOrDefault() is { } first && first.Mentions(accountUuid)) chosen = first;
        else if (ordered.FirstOrDefault() is { } f2 && !f2.IsExpired(now) && cachedPath == f2.PathString) chosen = f2;
        else (chosen, _) = await DesktopTokenParser.ProbeAsync(ordered.Where(c => !c.IsExpired(now)), accountUuid,
                                                                t => api.FetchProfileAsync(t, ct));
        if (chosen is null && ordered.Count == 1 && ordered[0].IsExpired(now)) chosen = ordered[0];

        if (chosen is not null)
        {
            if (chosen.IsExpired(now))
            {
                report.Issue = AccountIssue.ExpiredDesktop;
                return report;
            }
            report.Route = "token";
            await api.CallUsageAsync(chosen.AccessToken, report, AccountIssue.ExpiredDesktop, now, ct);
            if (report.Snapshot is not null)
            {
                // Save the choice after a confirmed 200, never for an expired or rejected pick.
                if (accountUuid is not null) report.DesktopChoice = (accountUuid, chosen.PathString);
                // The profile is needed only for a missing email.
                if (report.Email is null)
                {
                    try { report.Email = (await api.FetchProfileAsync(chosen.AccessToken, ct)).Profile?.Email; }
                    catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { }
                }
                return report;
            }
            // The token failed too (or there was no cookie): show the token route's issue.
            return report;
        }
        // No token to try: show the cookie route's issue (the cookie was already tried above).
        report.Issue = cookieIssue ?? AccountIssue.NotSignedIn("No Claude app sign-in token found");
        return report;
    }

    // MARK: Decryption

    /// <summary>The AES-256 key, unwrapped by DPAPI for this Windows user. Kept in memory only.</summary>
    byte[] Key()
    {
        if (key is not null) return key;
        byte[] wrapped;
        try { wrapped = ChromiumWin.StripDpapiPrefix(ChromiumWin.EncryptedKey(Paths.ReadShared(Path.Combine(Dir, "Local State")))); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new CredentialException(CredentialError.NotFound); }
        try { return key = ProtectedData.Unprotect(wrapped, null, DataProtectionScope.CurrentUser); }
        catch (CryptographicException) { throw new CredentialException(CredentialError.DecryptFailed); }
    }

    List<TokenCandidate> Candidates(DesktopConfig config)
    {
        var k = Key();
        var out_ = new List<TokenCandidate>();
        var decrypted = 0;
        foreach (var (name, value) in config.EncryptedCaches)
        {
            byte[] plain;
            try { plain = ChromiumWin.DecryptBase64(value, k); }
            catch (CredentialException) { continue; }
            decrypted++;
            var json = DesktopTokenParser.ParseJson(plain) ?? (plain.Length > 32 ? DesktopTokenParser.ParseJson(plain[32..]) : null);
            foreach (var c in DesktopTokenParser.Candidates(json))
                out_.Add(new TokenCandidate { Path = c.Path.Prepend(name).ToList(), AccessToken = c.AccessToken, ExpiresAt = c.ExpiresAt, Uuids = c.Uuids });
        }
        if (decrypted == 0) throw new CredentialException(CredentialError.DecryptFailed);
        return out_;
    }

    /// <summary>Key names and value types of the decrypted token cache, never values. For checking a new app version.</summary>
    public string ShapeDump()
    {
        var config = DesktopConfig.Parse(Paths.ReadShared(Path.Combine(Dir, "config.json")));
        var sb = new StringBuilder();
        foreach (var (name, value) in config.EncryptedCaches)
        {
            sb.AppendLine(name + ":");
            try { sb.AppendLine(DesktopTokenParser.Shape(DesktopTokenParser.ParseJson(ChromiumWin.DecryptBase64(value, Key())))); }
            catch (CredentialException e) { sb.AppendLine("  (" + e.Error + ")"); }
        }
        return sb.ToString();
    }

    /// <summary><c>sessionKey</c> for claude.ai from the app's Chromium cookie store, opened read-only and never copied.</summary>
    string? SessionKeyCookie()
    {
        var path = new[] { Path.Combine(Dir, "Network", "Cookies"), Path.Combine(Dir, "Cookies") }.FirstOrDefault(File.Exists);
        if (path is null) return null;
        var uri = ChromiumWin.SqliteFileUri(path) + "?mode=ro&immutable=1";
        if (Sqlite.sqlite3_open_v2(Encoding.UTF8.GetBytes(uri + "\0"), out var db, Sqlite.OPEN_READONLY | Sqlite.OPEN_URI, IntPtr.Zero) != 0)
        {
            Sqlite.sqlite3_close(db);
            Log.Info("desktop: cookie store not readable");
            return null;
        }
        try
        {
            const string sql = "SELECT host_key, encrypted_value, value FROM cookies WHERE name = 'sessionKey' AND host_key LIKE '%claude.ai' ORDER BY expires_utc DESC LIMIT 1";
            if (Sqlite.sqlite3_prepare_v2(db, Encoding.UTF8.GetBytes(sql + "\0"), -1, out var stmt, IntPtr.Zero) != 0) return null;
            try
            {
                if (Sqlite.sqlite3_step(stmt) != Sqlite.ROW) return null;
                var host = Marshal.PtrToStringUTF8(Sqlite.sqlite3_column_text(stmt, 0)) ?? "";
                if (Marshal.PtrToStringUTF8(Sqlite.sqlite3_column_text(stmt, 2)) is { Length: > 0 } plainText) return plainText;
                var n = Sqlite.sqlite3_column_bytes(stmt, 1);
                if (n <= 0) return null;
                var blob = new byte[n];
                Marshal.Copy(Sqlite.sqlite3_column_blob(stmt, 1), blob, 0, n);
                return Encoding.UTF8.GetString(ChromiumWin.StripHostDigest(ChromiumWin.Decrypt(blob, Key()), host));
            }
            finally { Sqlite.sqlite3_finalize(stmt); }
        }
        finally { Sqlite.sqlite3_close(db); }
    }

    /// <summary>Windows' own SQLite (System32\winsqlite3.dll), so no package is needed.</summary>
    static class Sqlite
    {
        public const int OPEN_READONLY = 0x1, OPEN_URI = 0x40, ROW = 100;
        [DllImport("winsqlite3.dll")] public static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
        [DllImport("winsqlite3.dll")] public static extern int sqlite3_close(IntPtr db);
        [DllImport("winsqlite3.dll")] public static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int bytes, out IntPtr stmt, IntPtr tail);
        [DllImport("winsqlite3.dll")] public static extern int sqlite3_step(IntPtr stmt);
        [DllImport("winsqlite3.dll")] public static extern int sqlite3_finalize(IntPtr stmt);
        [DllImport("winsqlite3.dll")] public static extern IntPtr sqlite3_column_text(IntPtr stmt, int col);
        [DllImport("winsqlite3.dll")] public static extern IntPtr sqlite3_column_blob(IntPtr stmt, int col);
        [DllImport("winsqlite3.dll")] public static extern int sqlite3_column_bytes(IntPtr stmt, int col);
    }
}
