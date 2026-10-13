using RadialLauncher.Models;
using RadialLauncher.Services;
using Xunit;

namespace RadialLauncher.Tests;

/// <summary>AppProfile.Matches 的进程名 / 窗口标题匹配逻辑。</summary>
public class AppProfileTests
{
    private static AppProfile MakeProfile(
        bool enabled = true,
        List<string>? processNames = null,
        List<string>? titleKeywords = null) => new()
    {
        Name = "测试",
        Enabled = enabled,
        ProcessNames = processNames ?? new() { "chrome" },
        TitleKeywords = titleKeywords ?? new(),
    };

    [Fact]
    public void Matches_TrueWhenProcessNameMatchesCaseInsensitively()
    {
        var profile = MakeProfile(processNames: new() { "chrome" });

        Assert.True(profile.Matches("chrome", "任意标题"));
        Assert.True(profile.Matches("CHROME", "任意标题"));
        Assert.True(profile.Matches("Chrome", "任意标题"));
    }

    [Fact]
    public void Matches_FalseWhenProcessNameDiffers()
    {
        var profile = MakeProfile(processNames: new() { "chrome" });
        Assert.False(profile.Matches("firefox", "任意标题"));
    }

    [Fact]
    public void Matches_FalseWhenDisabled()
    {
        var profile = MakeProfile(enabled: false, processNames: new() { "chrome" });
        Assert.False(profile.Matches("chrome", "标题"));
    }

    [Fact]
    public void Matches_FalseWhenNoProcessNamesConfigured()
    {
        var profile = MakeProfile(processNames: new());
        Assert.False(profile.Matches("chrome", "标题"));
    }

    [Fact]
    public void Matches_TrueWhenProcessMatchesAndNoTitleKeywords()
    {
        var profile = MakeProfile(processNames: new() { "code" }, titleKeywords: new());
        Assert.True(profile.Matches("code", "program.cs - Visual Studio Code"));
    }

    [Fact]
    public void Matches_RequiresTitleKeywordWhenConfigured()
    {
        var profile = MakeProfile(
            processNames: new() { "chrome" },
            titleKeywords: new() { "GitHub" });

        Assert.True(profile.Matches("chrome", "Pull requests · GitHub"));
        Assert.False(profile.Matches("chrome", "某个不相关的页面"));
    }

    [Fact]
    public void Matches_TitleKeywordIsCaseInsensitive()
    {
        var profile = MakeProfile(
            processNames: new() { "chrome" },
            titleKeywords: new() { "github" });

        Assert.True(profile.Matches("chrome", "GitHub - 首页"));
    }

    [Fact]
    public void Matches_MatchesAnyOfMultipleTitleKeywords()
    {
        var profile = MakeProfile(
            processNames: new() { "chrome" },
            titleKeywords: new() { "GitHub", "Stack Overflow" });

        Assert.True(profile.Matches("chrome", "Stack Overflow - Where Developers Learn"));
    }

    [Fact]
    public void Matches_IgnoresBlankTitleKeywords()
    {
        var profile = MakeProfile(
            processNames: new() { "chrome" },
            titleKeywords: new() { "", "   ", "GitHub" });

        // 空关键字被跳过，仍能靠 "GitHub" 命中
        Assert.True(profile.Matches("chrome", "GitHub"));
        // 只有空白关键字时，没有任何有效关键字能命中
        Assert.False(profile.Matches("chrome", "其他"));
    }

    [Fact]
    public void Matches_OnlyBlankKeywordsMeansNoMatch()
    {
        var profile = MakeProfile(
            processNames: new() { "chrome" },
            titleKeywords: new() { "", "  " });

        // TitleKeywords.Count != 0，因此进入关键字分支，但全部空白 → 不匹配
        Assert.False(profile.Matches("chrome", "任何标题"));
    }

    [Fact]
    public void Matches_MatchesAnyOfMultipleProcessNames()
    {
        var profile = MakeProfile(processNames: new() { "chrome", "msedge", "firefox" });
        Assert.True(profile.Matches("firefox", "标题"));
    }

    // ==================== Clone / ToString ====================

    [Fact]
    public void Clone_ProducesIndependentCopy()
    {
        var original = MakeProfile(
            processNames: new() { "chrome" },
            titleKeywords: new() { "GitHub" });
        original.Items.Add(new WheelItem { Name = "原始项" });

        var clone = original.Clone();
        clone.ProcessNames.Add("msedge");
        clone.Items[0].Name = "被修改";

        Assert.Single(original.ProcessNames);
        Assert.Equal("原始项", original.Items[0].Name);
    }

    [Fact]
    public void ToString_FallsBackToPlaceholderWhenNameMissing()
    {
        var profile = new AppProfile { Name = "", Icon = "" };
        Assert.Contains("(未命名)", profile.ToString());
    }

    // ==================== CreateDefaults ====================

    [Fact]
    public void CreateDefaults_ReturnsThreeProfiles()
    {
        var defaults = AppProfile.CreateDefaults();
        Assert.Equal(3, defaults.Count);
    }

    [Fact]
    public void CreateDefaults_AllProfilesHaveProcessNames()
    {
        foreach (var profile in AppProfile.CreateDefaults())
        {
            Assert.NotEmpty(profile.ProcessNames);
            Assert.True(profile.Enabled);
            Assert.NotEmpty(profile.Items);
        }
    }

    [Fact]
    public void CreateDefaults_BrowserProfileMatchesChrome()
    {
        var browser = AppProfile.CreateDefaults()[0];
        Assert.True(browser.Matches("chrome", "任意页面"));
    }
}
