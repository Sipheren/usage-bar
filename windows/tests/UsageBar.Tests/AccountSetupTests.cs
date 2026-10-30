using UsageBar.Core;

namespace UsageBar.Tests;

/// <summary>Mirrors macos/Tests/UsageBarTests/AccountSetupTests.swift (decision 16), with Windows' two sources.</summary>
public class FirstRunAccountsTests
{
    // Synthetic ids from scripts/secret-scan-allow.txt.
    const string CliUuid = "00000000-0000-4000-8000-0000000000aa";
    const string AppUuid = "00000000-0000-4000-8000-0000000000bb";

    static IEnumerable<SourceKind> Kinds(FoundSignIns found) => AppSettings.FirstRun(found).Accounts.Select(a => a.Source.Kind);

    [Fact]
    public void NothingFoundCreatesNoAccounts()
    {
        var s = AppSettings.FirstRun(new FoundSignIns());
        Assert.Empty(s.Accounts);
        Assert.Equal(LabelStyle.Bars, s.LabelStyle);
        Assert.Equal(DropdownLayout.BothAccounts, s.DropdownLayout);
        Assert.Equal("AUD", s.Currency);
    }

    [Fact]
    public void ClaudeCodeOnlyCreatesPersonal()
    {
        var s = AppSettings.FirstRun(new FoundSignIns(ClaudeCodeLogin: true, ClaudeCodeAccountUuid: CliUuid));
        var p = Assert.Single(s.Accounts);
        Assert.Equal("Personal", p.Name);
        Assert.Equal("P", p.Tag);
        Assert.Equal(SourceKind.ClaudeCode, p.Source.Kind);
        Assert.Equal(Path.Combine(Paths.DefaultClaudeConfigDir, "projects"), p.LogsDir);
        Assert.True(p.ShowInTray);
    }

    [Fact]
    public void ClaudeCodeMetadataWithoutTheCredentialsFileIsNotASignIn()
    {
        // ~/.claude.json can outlive a sign-out; only the credentials file counts.
        Assert.Empty(Kinds(new FoundSignIns(ClaudeCodeLogin: false, ClaudeCodeAccountUuid: CliUuid)));
    }

    [Fact]
    public void ClaudeAppOnlyCreatesTheClaudeAppAccount()
    {
        var s = AppSettings.FirstRun(new FoundSignIns(ClaudeAppHasTokenCache: true, ClaudeAppAccountUuid: AppUuid));
        var c = Assert.Single(s.Accounts);
        Assert.Equal("Claude app", c.Name);
        Assert.Equal("C", c.Tag);
        Assert.Equal(SourceKind.ClaudeDesktop, c.Source.Kind);
        Assert.Null(c.LogsDir);
    }

    [Fact]
    public void BothFoundCreatesPersonalThenTheClaudeApp()
    {
        var s = AppSettings.FirstRun(new FoundSignIns(true, CliUuid, true, AppUuid));
        Assert.Equal(new[] { "P", "C" }, s.Accounts.Select(a => a.Tag));
        Assert.Equal(new[] { SourceKind.ClaudeCode, SourceKind.ClaudeDesktop }, s.Accounts.Select(a => a.Source.Kind));
        Assert.NotEqual(s.Accounts[0].Id, s.Accounts[1].Id);
    }

    [Fact]
    public void TheClaudeAppOnTheClaudeCodeAccountIsSkipped()
    {
        Assert.Equal(new[] { SourceKind.ClaudeCode }, Kinds(new FoundSignIns(true, CliUuid, true, CliUuid)));
    }

    [Fact]
    public void SameAccountMatchIgnoresCase()
    {
        Assert.Equal(new[] { SourceKind.ClaudeCode }, Kinds(new FoundSignIns(true, CliUuid.ToUpperInvariant(), true, CliUuid)));
    }

    [Fact]
    public void TheClaudeAppIsAddedWhenClaudeCodeIsNotSignedIn()
    {
        // One account per unique sign-in found: with Claude Code signed out, a leftover ~/.claude.json naming the same
        // account doesn't make the Claude app a duplicate, so it is the one account.
        Assert.Equal(new[] { SourceKind.ClaudeDesktop }, Kinds(new FoundSignIns(false, CliUuid, true, CliUuid)));
    }

    [Fact]
    public void TheClaudeAppNeedsBothATokenCacheAndAnAccount()
    {
        Assert.Empty(Kinds(new FoundSignIns(ClaudeAppHasTokenCache: false, ClaudeAppAccountUuid: AppUuid)));
        Assert.Empty(Kinds(new FoundSignIns(ClaudeAppHasTokenCache: true, ClaudeAppAccountUuid: null)));
        Assert.Empty(Kinds(new FoundSignIns(ClaudeAppHasTokenCache: true, ClaudeAppAccountUuid: "")));
    }

    [Fact]
    public void ZeroAccountsSurviveSaveAndLoad()
    {
        var loaded = AppSettings.FromJson(AppSettings.FirstRun(new FoundSignIns()).ToJson());
        Assert.NotNull(loaded);
        Assert.Empty(loaded!.Accounts);
    }
}

public class SignInDetectorTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "usagebar-detect-" + Guid.NewGuid().ToString("N"));
    string ConfigDir => Path.Combine(root, ".claude");
    string ClaudeJson => Path.Combine(root, ".claude.json");
    string AppDir => Path.Combine(root, "Claude");

    public SignInDetectorTests()
    {
        Directory.CreateDirectory(ConfigDir);
        Directory.CreateDirectory(AppDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch (IOException) { }
    }

    FoundSignIns Detect() => SignInDetector.Detect(ConfigDir, ClaudeJson, AppDir);

    [Fact]
    public void NothingThereFindsNothing()
    {
        Assert.Equal(new FoundSignIns(), Detect());
    }

    [Fact]
    public void TheCredentialsFileCountsWithoutBeingRead()
    {
        // Not JSON: detection only checks that the file exists, it never parses the token.
        File.WriteAllText(Path.Combine(ConfigDir, ".credentials.json"), "not read");
        File.WriteAllText(ClaudeJson, """{"oauthAccount":{"accountUuid":"00000000-0000-4000-8000-0000000000aa"}}""");
        var found = Detect();
        Assert.True(found.ClaudeCodeLogin);
        Assert.Equal("00000000-0000-4000-8000-0000000000aa", found.ClaudeCodeAccountUuid);
    }

    [Fact]
    public void TheClaudeAppNeedsANonEmptyV2CacheAndAnAccount()
    {
        File.WriteAllText(Path.Combine(AppDir, "config.json"),
                          """{"oauth:tokenCacheV2":"djEw","lastKnownAccountUuid":"00000000-0000-4000-8000-0000000000bb"}""");
        var found = Detect();
        Assert.True(found.ClaudeAppHasTokenCache);
        Assert.Equal("00000000-0000-4000-8000-0000000000bb", found.ClaudeAppAccountUuid);

        File.WriteAllText(Path.Combine(AppDir, "config.json"), """{"oauth:tokenCache":"djEw","lastKnownAccountUuid":"x"}""");
        Assert.False(Detect().ClaudeAppHasTokenCache);
        File.WriteAllText(Path.Combine(AppDir, "config.json"), """{"oauth:tokenCacheV2":""}""");
        Assert.False(Detect().ClaudeAppHasTokenCache);
    }

    [Fact]
    public void UnreadableFilesCountAsNotFound()
    {
        File.WriteAllText(ClaudeJson, "{ not json");
        File.WriteAllText(Path.Combine(AppDir, "config.json"), "{ not json");
        Assert.Equal(new FoundSignIns(), Detect());
    }
}

public class DefaultAccountTests
{
    [Fact]
    public void OneDefaultPerSourceWithTheSettingsNamesAndTags()
    {
        var p = Account.DefaultAccount(SourceKind.ClaudeCode);
        var c = Account.DefaultAccount(SourceKind.ClaudeDesktop);
        Assert.Equal(("Personal", "P", SourceKind.ClaudeCode), (p.Name, p.Tag, p.Source.Kind));
        Assert.Equal(("Claude app", "C", SourceKind.ClaudeDesktop), (c.Name, c.Tag, c.Source.Kind));
        Assert.NotNull(p.LogsDir);
        Assert.Null(c.LogsDir);
    }

    [Fact]
    public void EachSourceGetsItsOwnColour()
    {
        Assert.Equal("orange", Account.DefaultAccount(SourceKind.ClaudeCode).Colour);
        Assert.Equal("purple", Account.DefaultAccount(SourceKind.ClaudeDesktop).Colour);
    }

    [Fact]
    public void OnlyTheRealSourcesCanBeAdded()
    {
        Assert.Equal(new[] { SourceKind.ClaudeCode, SourceKind.ClaudeDesktop }, AccountSources.Addable);
    }
}

public class DropdownContentTests
{
    [Fact]
    public void NoAccountsShowsTheEmptyStateInEitherLayout()
    {
        Assert.Equal(DropdownView.Empty, DropdownContent.For(DropdownLayout.BothAccounts, 0).View);
        Assert.Equal(DropdownView.Empty, DropdownContent.For(DropdownLayout.OneAtATime, 0).View);
    }

    [Fact]
    public void AllAccountsStacksAnyNumber()
    {
        foreach (var n in new[] { 1, 2, 3, 8 })
            Assert.Equal(new DropdownContent(DropdownView.Stacked, false), DropdownContent.For(DropdownLayout.BothAccounts, n));
    }

    [Fact]
    public void OneAtATimeHidesTheTabsWithASingleAccount()
    {
        Assert.Equal(new DropdownContent(DropdownView.OneAtATime, false), DropdownContent.For(DropdownLayout.OneAtATime, 1));
        Assert.Equal(new DropdownContent(DropdownView.OneAtATime, true), DropdownContent.For(DropdownLayout.OneAtATime, 2));
        Assert.Equal(new DropdownContent(DropdownView.OneAtATime, true), DropdownContent.For(DropdownLayout.OneAtATime, 3));
    }
}
