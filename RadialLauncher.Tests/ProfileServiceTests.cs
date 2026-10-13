using RadialLauncher.Models;
using RadialLauncher.Services;
using Xunit;

namespace RadialLauncher.Tests;

/// <summary>ProfileService 的路由逻辑：全局轮盘 vs 应用专属轮盘。</summary>
public class ProfileServiceTests
{
    private static ForegroundWindowInfo Info(string process, string title = "") => new()
    {
        ProcessName = process,
        Title = title,
    };

    private static AppConfig MakeConfig(
        bool profilesEnabled,
        int globalItemCount = 8,
        List<WheelItem>? globalItems = null,
        List<AppProfile>? profiles = null)
    {
        var config = new AppConfig
        {
            Appearance = new AppearanceSettings { ItemCount = globalItemCount },
            Environment = new EnvironmentSettings { ProfilesEnabled = profilesEnabled },
            WheelItems = globalItems ?? WheelItem.CreateDefaults(),
            Profiles = profiles ?? new(),
        };
        return config;
    }

    // ==================== 未启用专属配置 ====================

    [Fact]
    public void Resolve_ReturnsGlobalWhenProfilesDisabled()
    {
        var config = MakeConfig(profilesEnabled: false, profiles: new()
        {
            new AppProfile
            {
                Name = "浏览器",
                ProcessNames = new() { "chrome" },
                Items = new() { new WheelItem { Name = "专属" } },
            },
        });

        var wheel = ProfileService.Resolve(config, Info("chrome"));

        Assert.False(wheel.IsProfile);
        Assert.Null(wheel.Profile);
        Assert.Same(config.WheelItems, wheel.Items);
        Assert.Equal(config.Appearance.ItemCount, wheel.ItemCount);
    }

    // ==================== 命中专属配置 ====================

    [Fact]
    public void Resolve_ReturnsProfileWhenProcessMatches()
    {
        var profileItems = new List<WheelItem> { new() { Name = "专属项" } };
        var profile = new AppProfile
        {
            Name = "VS Code",
            Icon = "🧑‍💻",
            ProcessNames = new() { "code" },
            Items = profileItems,
        };

        var config = MakeConfig(profilesEnabled: true, profiles: new() { profile });
        var wheel = ProfileService.Resolve(config, Info("code"));

        Assert.True(wheel.IsProfile);
        Assert.Same(profile, wheel.Profile);
        Assert.Same(profileItems, wheel.Items);
        Assert.Equal("VS Code", wheel.Title);
        Assert.Equal("🧑‍💻", wheel.Icon);
    }

    [Fact]
    public void Resolve_UsesGlobalItemCountWhenNoOverride()
    {
        var profile = new AppProfile
        {
            ProcessNames = new() { "code" },
            ItemCountOverride = 0,   // 跟随全局
            Items = new() { new WheelItem { Name = "项" } },
        };

        var config = MakeConfig(profilesEnabled: true, globalItemCount: 4, profiles: new() { profile });
        var wheel = ProfileService.Resolve(config, Info("code"));

        Assert.Equal(4, wheel.ItemCount);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    public void Resolve_HonoursValidItemCountOverride(int overrideCount)
    {
        var profile = new AppProfile
        {
            ProcessNames = new() { "code" },
            ItemCountOverride = overrideCount,
            Items = new() { new WheelItem { Name = "项" } },
        };

        // 全局设为 8，若 override 为 4 就应生效
        var config = MakeConfig(profilesEnabled: true, globalItemCount: 8, profiles: new() { profile });
        var wheel = ProfileService.Resolve(config, Info("code"));

        Assert.Equal(overrideCount, wheel.ItemCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(12)]
    public void Resolve_IgnoresInvalidItemCountOverride(int overrideCount)
    {
        var profile = new AppProfile
        {
            ProcessNames = new() { "code" },
            ItemCountOverride = overrideCount,
            Items = new() { new WheelItem { Name = "项" } },
        };

        var config = MakeConfig(profilesEnabled: true, globalItemCount: 4, profiles: new() { profile });
        var wheel = ProfileService.Resolve(config, Info("code"));

        Assert.Equal(4, wheel.ItemCount);
    }

    [Fact]
    public void Resolve_UsesGlobalItemsWhenUseGlobalItemsIsTrue()
    {
        var globalItems = WheelItem.CreateDefaults();
        var profile = new AppProfile
        {
            ProcessNames = new() { "code" },
            UseGlobalItems = true,
            Items = new() { new WheelItem { Name = "专属" } },
        };

        var config = MakeConfig(profilesEnabled: true, globalItems: globalItems, profiles: new() { profile });
        var wheel = ProfileService.Resolve(config, Info("code"));

        Assert.True(wheel.IsProfile);
        Assert.Same(globalItems, wheel.Items);
    }

    [Fact]
    public void Resolve_FallsBackToGlobalItemsWhenProfileItemsEmpty()
    {
        var globalItems = WheelItem.CreateDefaults();
        var profile = new AppProfile
        {
            Name = "空配置",
            ProcessNames = new() { "code" },
            UseGlobalItems = false,
            Items = new(),   // 空
        };

        var config = MakeConfig(profilesEnabled: true, globalItems: globalItems, profiles: new() { profile });
        var wheel = ProfileService.Resolve(config, Info("code"));

        // 仍算命中专属配置，但内容退回全局
        Assert.True(wheel.IsProfile);
        Assert.Same(globalItems, wheel.Items);
    }

    [Fact]
    public void Resolve_FirstMatchingProfileWins()
    {
        var first = new AppProfile
        {
            Name = "第一个",
            ProcessNames = new() { "code" },
            Items = new() { new WheelItem { Name = "A" } },
        };
        var second = new AppProfile
        {
            Name = "第二个",
            ProcessNames = new() { "code" },
            Items = new() { new WheelItem { Name = "B" } },
        };

        var config = MakeConfig(profilesEnabled: true, profiles: new() { first, second });
        var wheel = ProfileService.Resolve(config, Info("code"));

        Assert.Equal("第一个", wheel.Title);
    }

    [Fact]
    public void Resolve_SkipsNullProfileEntries()
    {
        var profile = new AppProfile
        {
            Name = "有效",
            ProcessNames = new() { "code" },
            Items = new() { new WheelItem { Name = "项" } },
        };

        var config = MakeConfig(profilesEnabled: true, profiles: new() { null!, profile });
        var wheel = ProfileService.Resolve(config, Info("code"));

        Assert.Equal("有效", wheel.Title);
    }

    [Fact]
    public void Resolve_FallsBackToGlobalWhenNoProfileMatches()
    {
        var profile = new AppProfile
        {
            Name = "VS Code",
            ProcessNames = new() { "code" },
            Items = new() { new WheelItem { Name = "项" } },
        };

        var config = MakeConfig(profilesEnabled: true, profiles: new() { profile });
        var wheel = ProfileService.Resolve(config, Info("notepad"));

        Assert.False(wheel.IsProfile);
        Assert.Same(config.WheelItems, wheel.Items);
    }

    [Fact]
    public void Resolve_TitleKeywordMustAlsoMatch()
    {
        var profile = new AppProfile
        {
            Name = "GitHub 标签页",
            ProcessNames = new() { "chrome" },
            TitleKeywords = new() { "GitHub" },
            Items = new() { new WheelItem { Name = "项" } },
        };

        var config = MakeConfig(profilesEnabled: true, profiles: new() { profile });

        Assert.True(ProfileService.Resolve(config, Info("chrome", "GitHub")).IsProfile);
        Assert.False(ProfileService.Resolve(config, Info("chrome", "其他页面")).IsProfile);
    }

    // ==================== FindByProcess ====================

    [Fact]
    public void FindByProcess_MatchesCaseInsensitively()
    {
        var profile = new AppProfile { Name = "浏览器", ProcessNames = new() { "chrome", "msedge" } };
        var config = MakeConfig(profilesEnabled: true, profiles: new() { profile });

        Assert.Same(profile, ProfileService.FindByProcess(config, "chrome"));
        Assert.Same(profile, ProfileService.FindByProcess(config, "CHROME"));
        Assert.Same(profile, ProfileService.FindByProcess(config, "MsEdge"));
    }

    [Fact]
    public void FindByProcess_ReturnsNullWhenNoMatch()
    {
        var profile = new AppProfile { Name = "浏览器", ProcessNames = new() { "chrome" } };
        var config = MakeConfig(profilesEnabled: true, profiles: new() { profile });

        Assert.Null(ProfileService.FindByProcess(config, "notepad"));
    }

    [Fact]
    public void FindByProcess_IgnoresProfilesEnabledFlag()
    {
        // 设置面板用它来定位，即使专属配置功能关着也应能查到
        var profile = new AppProfile { Name = "浏览器", ProcessNames = new() { "chrome" } };
        var config = MakeConfig(profilesEnabled: false, profiles: new() { profile });

        Assert.Same(profile, ProfileService.FindByProcess(config, "chrome"));
    }

    // ==================== ActiveWheel 属性 ====================

    [Fact]
    public void ActiveWheel_DefaultsToNotProfile()
    {
        var wheel = new ActiveWheel();
        Assert.False(wheel.IsProfile);
        Assert.Equal("", wheel.Title);
        Assert.Equal("", wheel.Icon);
        Assert.Equal(8, wheel.ItemCount);
    }
}
