using UsageBar.Core;

namespace UsageBar.Tests;

/// <summary>Every account colour must read as small tray text and bars, on both taskbars and in the flyout.</summary>
public class AccountColourTests
{
    const string DarkTaskbar = "#1C1C1C", DarkFlyout = "#2C2C2C", LightTaskbar = "#EEEEEE", LightFlyout = "#F3F3F3";

    [Fact]
    public void TenNamedPresets()
    {
        Assert.Equal(10, AccountColours.All.Count);
        Assert.Equal(10, AccountColours.All.Select(p => p.Id).Distinct().Count());
        Assert.Equal(10, AccountColours.All.Select(p => p.Name).Distinct().Count());
    }

    [Fact]
    public void EveryPresetIsReadableOnEveryBackground()
    {
        foreach (var p in AccountColours.All)
        {
            // Tray numbers are about 8 px tall: well above the 4.5:1 text minimum on the dark taskbar.
            Assert.True(AccountColours.Contrast(p.OnDark, DarkTaskbar) >= 6.8, $"{p.Name} on the dark taskbar");
            Assert.True(AccountColours.Contrast(p.OnDark, DarkFlyout) >= 4.5, $"{p.Name} in the dark flyout");
            Assert.True(AccountColours.Contrast(p.OnLight, LightTaskbar) >= 4.5, $"{p.Name} on the light taskbar");
            Assert.True(AccountColours.Contrast(p.OnLight, LightFlyout) >= 4.5, $"{p.Name} in the light flyout");
            Assert.True(AccountColours.Contrast("#FFFFFF", p.Badge) >= 4.5, $"white tag on the {p.Name} badge");
        }
    }

    [Fact]
    public void NoTwoPresetsLookAlike()
    {
        foreach (var a in AccountColours.All)
            foreach (var b in AccountColours.All.Where(b => b != a))
            {
                Assert.True(AccountColours.Distance(a.OnDark, b.OnDark) >= 20, $"{a.Name}/{b.Name} on dark");
                Assert.True(AccountColours.Distance(a.OnLight, b.OnLight) >= 20, $"{a.Name}/{b.Name} on light");
            }
    }

    [Fact]
    public void OldHexColoursMapToTheNearestPreset()
    {
        Assert.Equal("orange", AccountColours.Nearest("#D97757").Id);   // the Mac's clay
        Assert.Equal("blue", AccountColours.Nearest("#3E7CC4").Id);
        Assert.Equal("green", AccountColours.Nearest("#4C9A6A").Id);
        Assert.Equal("orange", AccountColours.Get("no-such-preset").Id);
    }

    [Fact]
    public void DefaultsAndBadges()
    {
        Assert.Equal("orange", Account.DefaultPersonal().Colour);
        Assert.Equal("blue", Account.DefaultWork().Colour);
        Assert.Equal(AccountColours.Get("orange").Badge, Account.DefaultPersonal().Preset.Badge);
    }

    [Fact]
    public void SettingsFromZeroPointOneKeepTheirColoursAsPresets()
    {
        var s = AppSettings.FromJson("""
        {"defaultsVersion":1,"accounts":[{"id":"00000000-0000-4000-8000-0000000000aa","name":"Personal","tag":"P",
          "colorHex":"#D97757","source":{"kind":"claudeCode"}},{"id":"00000000-0000-4000-8000-0000000000bb","name":"Job","tag":"J",
          "colorHex":"#4C9A6A","source":{"kind":"claudeCode"}}]}
        """);
        Assert.Equal(new[] { "orange", "green" }, s.Accounts.Select(a => a.Colour));
        Assert.DoesNotContain("colorHex", s.ToJson());
    }
}
