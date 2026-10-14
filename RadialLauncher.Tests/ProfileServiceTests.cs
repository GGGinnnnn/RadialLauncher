using RadialLauncher.Models;
using RadialLauncher.Services;
using Xunit;

namespace RadialLauncher.Tests;

/// <summary>ProfileService 的路由逻辑：全局轮盘 vs 应用专属轮盘。</summary>
public class ProfileServiceTests
{
    private static ForegroundWindowInfo Info(
        string process,
        string title = "",
        string className = "") => new()
    {
        ProcessName = process,
        Title = title,
        ClassName = className,
        // Shell 一律走真实判定，测试里不自己编一个 IsShell 出来 ——
        // 否则验的是「我以为的分类」，而不是被测代码实际看到的东西。
        Shell = ShellWindowDetection.Classify(process, className),
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

    [Fact]
    public void FindByProcess_HandlesNullProcessNames()
    {
        // 设置面板直接调用本方法，拿到的可能是刚反序列化、
        // 尚未经过 ConfigService.Normalize 的配置对象 —— ProcessNames 为 null 不应 NRE
        var broken = new AppProfile { Name = "半成品", ProcessNames = null! };
        var good = new AppProfile { Name = "有效", ProcessNames = new() { "code" } };
        var config = MakeConfig(profilesEnabled: true, profiles: new() { broken, good });

        Assert.Same(good, ProfileService.FindByProcess(config, "code"));
    }

    [Fact]
    public void FindByProcess_HandlesNullProfileEntries()
    {
        var good = new AppProfile { Name = "有效", ProcessNames = new() { "code" } };
        var config = MakeConfig(profilesEnabled: true, profiles: new() { null!, good });

        Assert.Same(good, ProfileService.FindByProcess(config, "code"));
    }

    [Fact]
    public void FindByProcess_HandlesAllBrokenEntries()
    {
        var config = MakeConfig(profilesEnabled: true, profiles: new()
        {
            null!,
            new AppProfile { Name = "无进程名", ProcessNames = null! },
        });

        Assert.Null(ProfileService.FindByProcess(config, "code"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void FindByProcess_ReturnsNullForBlankTarget(string? target)
    {
        var profile = new AppProfile { Name = "浏览器", ProcessNames = new() { "chrome" } };
        var config = MakeConfig(profilesEnabled: true, profiles: new() { profile });

        Assert.Null(ProfileService.FindByProcess(config, target!));
    }

    // ==================== shell 界面（桌面 / 任务栏） ====================

    /// <summary>
    /// 回归：桌面与任务栏也由 explorer.exe 承载，但它们是 shell 表面、不是资源管理器窗口。
    /// Shell 非 None 时必须退回全局轮盘，否则在桌面上会唤出文件夹专用轮盘。
    /// 窗口类名直接喂给真实判定函数，两层任何一层退化都会让用例变红。
    /// </summary>
    [Theory]
    [InlineData("Progman", ShellSurface.Desktop)]                 // 桌面
    [InlineData("WorkerW", ShellSurface.Desktop)]                 // 桌面（壁纸幻灯片 / 动态壁纸）
    [InlineData("Shell_TrayWnd", ShellSurface.Taskbar)]           // 主任务栏
    [InlineData("Shell_SecondaryTrayWnd", ShellSurface.Taskbar)]  // 扩展屏任务栏
    public void Resolve_FallsBackToGlobalWheelForShellSurfaces(string className, ShellSurface expected)
    {
        var profile = new AppProfile
        {
            Name = "资源管理器",
            ProcessNames = new() { "explorer" },
            Items = new() { new WheelItem { Name = "地址栏" } },
        };

        var config = MakeConfig(profilesEnabled: true, profiles: new() { profile });

        var info = Info("explorer", "", className);
        Assert.Equal(expected, info.Shell);   // 前提：这类窗口确实被判成 shell 表面
        Assert.True(info.IsShell);

        var wheel = ProfileService.Resolve(config, info);

        Assert.False(wheel.IsProfile);
        Assert.Null(wheel.Profile);
        Assert.Empty(wheel.Title);
        Assert.Same(config.WheelItems, wheel.Items);
    }

    /// <summary>
    /// 反向保护：真正的资源管理器窗口（窗口类 CabinetWClass，IsShell=false）必须仍命中
    /// 专属配置，防止上面的修复被扩大成“进程名是 explorer 就一律不匹配”。
    /// </summary>
    [Fact]
    public void Resolve_StillMatchesExplorerProfileForRealExplorerWindows()
    {
        var profileItems = new List<WheelItem> { new() { Name = "地址栏" } };
        var profile = new AppProfile
        {
            Name = "资源管理器",
            ProcessNames = new() { "explorer" },
            Items = profileItems,
        };

        var config = MakeConfig(profilesEnabled: true, profiles: new() { profile });
        var wheel = ProfileService.Resolve(
            config, Info("explorer", "下载", "CabinetWClass"));

        Assert.True(wheel.IsProfile);
        Assert.Same(profile, wheel.Profile);
        Assert.Same(profileItems, wheel.Items);
        Assert.Equal("资源管理器", wheel.Title);
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
