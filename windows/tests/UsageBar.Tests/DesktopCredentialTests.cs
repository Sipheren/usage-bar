using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using UsageBar.Core;

namespace UsageBar.Tests;

public class DesktopCredentialTests
{
    // Synthetic ids (allow-listed in scripts/secret-scan-allow.txt). The fake token is assembled at runtime
    // so the source never contains a token-shaped literal.
    const string AccountUuid = "00000000-0000-4000-8000-0000000000aa";
    const string OtherUuid = "00000000-0000-4000-8000-0000000000bb";
    const string ClientId = "00000000-0000-4000-8000-0000000000cc";
    static readonly string SecretToken = "sk-" + "ant-oat01-" + "FAKE-TOKEN-FOR-TESTS-ONLY-1234567890";

    static string Fake(char c) => new(c, 24);

    static JsonNode FakeCache() => new JsonObject
    {
        [$"{ClientId}:{OtherUuid}:user:inference"] = new JsonObject { ["token"] = "other-account-token-0000000000000000", ["refreshToken"] = "r1", ["expiresAt"] = 4_000_000_000_000 },
        [$"{ClientId}:{AccountUuid}:user:inference"] = new JsonObject { ["token"] = SecretToken, ["refreshToken"] = "r2", ["expiresAt"] = 4_000_000_000_000 },
        ["meta"] = new JsonObject { ["version"] = 2 },
    };

    static string PathOf(List<TokenCandidate> cands, string firstKey) => cands.First(c => c.Path[0] == firstKey).PathString;

    [Fact]
    public void FindsCandidatesAndPrefersTheDesktopAccount()
    {
        var cands = DesktopTokenParser.Candidates(FakeCache());
        Assert.Equal(2, cands.Count);
        var ordered = DesktopTokenParser.Ordered(cands, AccountUuid, null, DateTimeOffset.UtcNow);
        Assert.Equal(SecretToken, ordered[0].AccessToken);
        Assert.True(ordered[0].Mentions(AccountUuid));
        Assert.Equal(4_000_000_000, ordered[0].ExpiresAt!.Value.ToUnixTimeSeconds());
    }

    [Fact]
    public void FindsTokensInNestedEmbeddedJsonAndAlternateKeys()
    {
        var inner = new JsonObject { ["access_token"] = SecretToken, ["expires_at"] = "2099-01-01T00:00:00Z", ["accountUuid"] = AccountUuid }.ToJsonString();
        var outer = new JsonObject { ["entries"] = new JsonArray(new JsonObject { ["blob"] = inner }) };
        var cands = DesktopTokenParser.Candidates(outer);
        Assert.Single(cands);
        Assert.True(cands[0].Mentions(AccountUuid));
        Assert.NotNull(cands[0].ExpiresAt);
    }

    [Fact]
    public void UnexpiredCandidateBeatsExpiredCachedPath()
    {
        var now = DateTimeOffset.UtcNow;
        var cache = new JsonObject
        {
            ["cached"] = new JsonObject { ["accessToken"] = Fake('c'), ["expiresAt"] = now.AddHours(-1).ToUnixTimeMilliseconds() },
            ["fresh"] = new JsonObject { ["accessToken"] = Fake('f'), ["expiresAt"] = now.AddHours(1).ToUnixTimeMilliseconds() },
        };
        var cands = DesktopTokenParser.Candidates(cache);
        var ordered = DesktopTokenParser.Ordered(cands, null, PathOf(cands, "cached"), now);
        Assert.Equal("fresh", ordered[0].Path[0]);
        Assert.False(ordered[0].IsExpired(now));
    }

    [Fact]
    public void UnexpiredCachedPathStillLeadsOtherUnexpired()
    {
        var now = DateTimeOffset.UtcNow;
        var future = now.AddHours(1).ToUnixTimeMilliseconds();
        var cache = new JsonObject
        {
            ["cached"] = new JsonObject { ["accessToken"] = Fake('c'), ["expiresAt"] = future },
            ["other"] = new JsonObject { ["accessToken"] = Fake('o'), ["expiresAt"] = future + 10_000 },
        };
        var cands = DesktopTokenParser.Candidates(cache);
        Assert.Equal("cached", DesktopTokenParser.Ordered(cands, null, PathOf(cands, "cached"), now)[0].Path[0]);
    }

    [Fact]
    public void ExpiredCandidatesSortAfterLiveOnes()
    {
        var cache = new JsonObject
        {
            ["a"] = new JsonObject { ["accessToken"] = Fake('a'), ["expiresAt"] = 1_000_000_000_000 },
            ["b"] = new JsonObject { ["accessToken"] = Fake('b'), ["expiresAt"] = 4_000_000_000_000 },
        };
        var now = DateTimeOffset.UtcNow;
        var ordered = DesktopTokenParser.Ordered(DesktopTokenParser.Candidates(cache), null, null, now);
        Assert.Equal(new[] { "b", "a" }, ordered.Select(c => c.Path[0]));
        Assert.True(ordered[1].IsExpired(now));
    }

    [Fact]
    public void ShapeDumpShowsKeysAndTypesButNoValues()
    {
        var shape = DesktopTokenParser.Shape(FakeCache());
        Assert.DoesNotContain(SecretToken, shape);
        Assert.DoesNotContain("other-account-token", shape);
        Assert.DoesNotContain("4000000000000", shape);
        Assert.Contains($"token: string(len {SecretToken.Length}, sk-ant token)", shape);
        Assert.Contains("expiresAt: number(int) epoch-ms-like", shape);
        Assert.Contains("<uuid 00000000…>", shape);
        Assert.DoesNotContain(AccountUuid, shape);
    }

    [Fact]
    public void CandidateDescriptionNeverIncludesToken()
    {
        foreach (var c in DesktopTokenParser.Candidates(FakeCache())) Assert.DoesNotContain(c.AccessToken, c.ToString());
    }

    [Fact]
    public void MaskedKeyHidesOpaqueRuns()
    {
        Assert.Equal("abc:<opaque len 40>", DesktopTokenParser.MaskedKey("abc:" + new string('Z', 40)));
        Assert.Equal("<secret-like key>", DesktopTokenParser.MaskedKey("sk-ant-xyz"));
    }

    [Fact]
    public void WindowsV10IsAesGcmWithTheDpapiKey()
    {
        // Chromium on Windows: "v10" + 12-byte nonce + ciphertext + 16-byte tag, AES-256-GCM.
        var key = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes("""{"hello":"world"}""");
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var gcm = new AesGcm(key, 16)) gcm.Encrypt(nonce, plain, cipher, tag);
        var blob = "v10"u8.ToArray().Concat(nonce).Concat(cipher).Concat(tag).ToArray();

        Assert.Equal(plain, ChromiumWin.Decrypt(blob, key));
        Assert.Equal(plain, ChromiumWin.DecryptBase64(Convert.ToBase64String(blob), key));
        // A wrong key fails the tag check instead of producing garbage.
        Assert.Throws<CredentialException>(() => ChromiumWin.Decrypt(blob, RandomNumberGenerator.GetBytes(32)));
        Assert.Throws<CredentialException>(() => ChromiumWin.Decrypt("v20"u8.ToArray().Concat(nonce).Concat(cipher).Concat(tag).ToArray(), key));
    }

    [Fact]
    public void UnwrapsTheLocalStateKeyPrefix()
    {
        var wrapped = "DPAPI"u8.ToArray().Concat(new byte[] { 1, 2, 3 }).ToArray();
        Assert.Equal(new byte[] { 1, 2, 3 }, ChromiumWin.StripDpapiPrefix(wrapped));
        Assert.Throws<CredentialException>(() => ChromiumWin.StripDpapiPrefix(new byte[] { 1, 2, 3 }));
        var localState = """{"os_crypt":{"audit_enabled":true,"encrypted_key":"RFBBUEkBAgM="}}""";
        Assert.Equal("DPAPI"u8.ToArray().Concat(new byte[] { 1, 2, 3 }), ChromiumWin.EncryptedKey(Encoding.UTF8.GetBytes(localState)));
    }

    [Fact]
    public void StripsHostDigestPrefix()
    {
        const string host = ".claude.ai";
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(host));
        Assert.Equal("abc"u8.ToArray(), ChromiumWin.StripHostDigest(digest.Concat("abc"u8.ToArray()).ToArray(), host));
        Assert.Equal("abc"u8.ToArray(), ChromiumWin.StripHostDigest("abc"u8.ToArray(), host));
    }

    [Fact]
    public void ReadsTheDesktopConfig()
    {
        var cfg = DesktopConfig.Parse(Encoding.UTF8.GetBytes($$"""
        {"lastKnownAccountUuid":"{{AccountUuid}}","oauth:tokenCacheV2":"djEwAAA=","oauth:tokenCache":"","other":1}
        """));
        Assert.Equal(AccountUuid, cfg.LastKnownAccountUuid);
        Assert.Equal(new[] { "oauth:tokenCacheV2" }, cfg.EncryptedCaches.Select(c => c.Key));
    }
}
