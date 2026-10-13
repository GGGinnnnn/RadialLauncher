using RadialLauncher.Models;
using RadialLauncher.Services;
using Xunit;

namespace RadialLauncher.Tests;

/// <summary>ConfigService.Normalize 的纯逻辑测试（不触盘）。</summary>
public class ConfigServiceNormalizeTests
{
    [Fact]
    public void Normalize_ForcesSchemaVersionTwo()
    {
        var config = new AppConfig { SchemaVersion = 1 };
        ConfigService.Normalize(config);
        Assert.Equal(2, config.SchemaVersion);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(12)]
    public void Normalize_ItemCount_OnlyAllowsFourOrEight(int input)
    {
        var config = new AppConfig();
        config.Appearance.ItemCount = input;
        ConfigService.Normalize(config);

        Assert.True(config.Appearance.ItemCount is 4 or 8);
        Assert.Equal(8, config.Appearance.ItemCount); // 非法值回落到 8
    }

    [Theory]
    [InlineData(4, 4)]
    [InlineData(8, 8)]
    public void Normalize_ItemCount_KeepsValidValues(int input, int expected)
    {
        var config = new AppConfig();
        config.Appearance.ItemCount = input;
        ConfigService.Normalize(config);
        Assert.Equal(expected, config.Appearance.ItemCount);
    }

    [Theory]
    [InlineData(0.1, 0.6)]     // 低于下限
    [InlineData(3.0, 1.6)]     // 高于上限
    [InlineData(1.0, 1.0)]     // 合法
    [InlineData(1.6, 1.6)]     // 边界内
    public void Normalize_Scale_Clamped(double input, double expected)
    {
        var config = new AppConfig();
        config.Appearance.Scale = input;
        ConfigService.Normalize(config);
        Assert.Equal(expected, config.Appearance.Scale);
    }

    [Theory]
    [InlineData(10, 44)]      // 低于下限
    [InlineData(200, 120)]    // 高于上限
    [InlineData(74, 74)]
    public void Normalize_ItemSize_Clamped(double input, double expected)
    {
        var config = new AppConfig();
        config.Appearance.ItemSize = input;
        ConfigService.Normalize(config);
        Assert.Equal(expected, config.Appearance.ItemSize);
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(500, 120)]
    [InlineData(34, 34)]
    public void Normalize_DeadZone_Clamped(double input, double expected)
    {
        var config = new AppConfig();
        config.Appearance.DeadZone = input;
        ConfigService.Normalize(config);
        Assert.Equal(expected, config.Appearance.DeadZone);
    }

    [Theory]
    [InlineData(0.0, 0.3)]
    [InlineData(2.0, 1.0)]
    [InlineData(0.8, 0.8)]
    public void Normalize_Opacity_Clamped(double input, double expected)
    {
        var config = new AppConfig();
        config.Appearance.Opacity = input;
        ConfigService.Normalize(config);
        Assert.Equal(expected, config.Appearance.Opacity);
    }

    [Theory]
    [InlineData(0, 60)]         // 低于下限 → 收敛到 60
    [InlineData(10, 60)]
    [InlineData(59, 60)]
    [InlineData(60, 60)]        // 边界值
    [InlineData(5000, 1500)]    // 超上限
    [InlineData(1500, 1500)]    // 上限边界
    [InlineData(300, 300)]      // 合法
    [InlineData(220, 220)]      // 默认值
    public void Normalize_HoldThreshold_Clamped(int input, int expected)
    {
        var config = new AppConfig();
        config.Trigger.HoldThresholdMs = input;
        ConfigService.Normalize(config);
        Assert.Equal(expected, config.Trigger.HoldThresholdMs);
    }

    [Fact]
    public void Normalize_EnablesCtrlWhenNoTriggerKeySelected()
    {
        var config = new AppConfig();
        config.Trigger.ByCtrl = false;
        config.Trigger.ByWin = false;
        config.Trigger.ByMiddleMouse = false;

        ConfigService.Normalize(config);

        Assert.True(config.Trigger.ByCtrl);
    }

    [Fact]
    public void Normalize_KeepsExistingTriggerWhenOneIsEnabled()
    {
        var config = new AppConfig();
        config.Trigger.ByCtrl = false;
        config.Trigger.ByWin = true;
        config.Trigger.ByMiddleMouse = false;

        ConfigService.Normalize(config);

        Assert.False(config.Trigger.ByCtrl);
        Assert.True(config.Trigger.ByWin);
    }

    [Theory]
    [InlineData(null, "zh-CN")]
    [InlineData("", "zh-CN")]
    [InlineData("   ", "zh-CN")]
    [InlineData("en", "en")]
    public void Normalize_Language_DefaultsToChinese(string? input, string expected)
    {
        var config = new AppConfig();
        config.System.Language = input!;
        ConfigService.Normalize(config);
        Assert.Equal(expected, config.System.Language);
    }

    [Fact]
    public void Normalize_GitHubUrl_DefaultsWhenEmpty()
    {
        var config = new AppConfig();
        config.System.GitHubUrl = "";
        ConfigService.Normalize(config);
        Assert.False(string.IsNullOrWhiteSpace(config.System.GitHubUrl));
    }

    [Fact]
    public void Normalize_DoesNotOverrideCustomGitHubUrl()
    {
        var config = new AppConfig();
        config.System.GitHubUrl = "https://example.com/repo";
        ConfigService.Normalize(config);
        Assert.Equal("https://example.com/repo", config.System.GitHubUrl);
    }

    [Fact]
    public void Normalize_NullCollectionsAreBackfilled()
    {
        var config = new AppConfig
        {
            Environment = null!,
            WheelItems = null!,
            Profiles = null!,
        };

        ConfigService.Normalize(config);

        Assert.NotNull(config.Environment);
        Assert.NotNull(config.WheelItems);
        Assert.NotNull(config.Profiles);
        Assert.NotNull(config.Environment.BlacklistProcesses);
        Assert.NotNull(config.Environment.WhitelistProcesses);
    }

    [Fact]
    public void Normalize_NullProcessListFieldsAreBackfilled()
    {
        // 反序列化出的 JSON 里显式写了 null 时会出现这种情况
        var config = new AppConfig();
        config.Environment.BlacklistProcesses = null!;
        config.Environment.WhitelistProcesses = null!;

        ConfigService.Normalize(config);

        Assert.NotNull(config.Environment.BlacklistProcesses);
        Assert.NotNull(config.Environment.WhitelistProcesses);
    }

    [Fact]
    public void Normalize_NullWheelItemsBackfilledButEmptyListIsPreserved()
    {
        // ??= 只兜底 null；显式的空列表被保留（用户可能就是想要空轮盘）
        var config = new AppConfig { WheelItems = null! };
        ConfigService.Normalize(config);
        Assert.Equal(WheelItem.CreateDefaults().Count, config.WheelItems.Count);

        var empty = new AppConfig { WheelItems = new() };
        ConfigService.Normalize(empty);
        Assert.Empty(empty.WheelItems);
    }

    [Fact]
    public void Normalize_NormalizesEachWheelItem()
    {
        var config = new AppConfig
        {
            WheelItems = new()
            {
                new WheelItem { Name = null!, Icon = null!, ActionType = null!, ActionValue = null! },
            },
        };

        ConfigService.Normalize(config);

        var item = config.WheelItems[0];
        Assert.NotNull(item.Name);
        Assert.NotNull(item.Icon);
        Assert.NotNull(item.ActionType);
        Assert.NotNull(item.ActionValue);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Normalize_NaNOrInfinityScale_UsesFallback(double value)
    {
        var config = new AppConfig();
        config.Appearance.Scale = value;
        ConfigService.Normalize(config);
        Assert.Equal(1.0, config.Appearance.Scale);
    }

    [Fact]
    public void Normalize_IsIdempotent()
    {
        var config = new AppConfig
        {
            Appearance = new AppearanceSettings
            {
                ItemCount = 5,
                Scale = 99,
                ItemSize = -1,
                DeadZone = 999,
                Opacity = 5,
            },
        };
        config.Trigger.ByCtrl = false;
        config.Trigger.ByWin = false;
        config.Trigger.ByMiddleMouse = false;
        config.Trigger.HoldThresholdMs = -10;

        ConfigService.Normalize(config);
        var first = (config.Appearance.ItemCount, config.Appearance.Scale,
                     config.Appearance.ItemSize, config.Appearance.DeadZone,
                     config.Appearance.Opacity, config.Trigger.HoldThresholdMs);

        ConfigService.Normalize(config);
        var second = (config.Appearance.ItemCount, config.Appearance.Scale,
                      config.Appearance.ItemSize, config.Appearance.DeadZone,
                      config.Appearance.Opacity, config.Trigger.HoldThresholdMs);

        Assert.Equal(first, second);
    }

    // ==================== 已知缺口（回归护栏）====================

    [Fact]
    public void Normalize_DoesNotBackfillNullAppearanceGroup()
    {
        // Normalize 只对 Environment / WheelItems / Profiles 做了 null 兜底，
        // 没有对 Appearance / Trigger / System 兜底。
        // 如果 config.json 里显式写了 "Appearance": null，反序列化后这里会是 null，
        // Normalize 会在读取 c.Appearance.ItemCount 时抛 NRE。
        // 本测试记录当前行为：一旦补上兜底，这里会失败，正好提醒更新测试。
        var config = new AppConfig { Appearance = null! };

        Assert.Throws<NullReferenceException>(() => ConfigService.Normalize(config));
    }
}
