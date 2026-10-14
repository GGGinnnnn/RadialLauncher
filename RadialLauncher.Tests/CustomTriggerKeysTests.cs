using RadialLauncher.Services;
using Xunit;

namespace RadialLauncher.Tests;

/// <summary>
/// CustomTriggerKeys —— 自定义触发键的名字表与校验。
///
/// 它决定“用户填进去的名字能不能用”，以及“落到钩子里的是哪些虚拟键码”。
/// 这一段是纯逻辑，可以直接钉住，不需要装钩子。
/// </summary>
public class CustomTriggerKeysTests
{
    // ==================== IsAllowed ====================

    [Theory]
    [InlineData("Shift")]
    [InlineData("CapsLock")]
    [InlineData("NumLock")]
    [InlineData("ScrollLock")]
    [InlineData("Apps")]
    [InlineData("F1")]
    [InlineData("F13")]
    [InlineData("F24")]
    [InlineData("Tab")]
    [InlineData("PageUp")]
    [InlineData("VolumeUp")]
    [InlineData("  F13  ")]          // 前后空白应当被容忍
    public void IsAllowed_TrueForUsableKeys(string name)
    {
        Assert.True(CustomTriggerKeys.IsAllowed(name), $"{name} 应当允许作为自定义触发键");
    }

    [Theory]
    [InlineData("Ctrl")]
    [InlineData("Control")]
    [InlineData("Win")]
    [InlineData("Windows")]
    [InlineData("Meta")]
    [InlineData("Alt")]
    [InlineData("Menu")]
    [InlineData("Esc")]
    [InlineData("Escape")]
    public void IsAllowed_FalseForReservedKeys(string name)
    {
        // 这些键已经有专属处理（预设触发键 / Esc 取消），再加进自定义列表只会是死配置
        Assert.False(CustomTriggerKeys.IsAllowed(name), $"{name} 是保留键名，不应允许");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("不存在的键")]
    [InlineData("F25")]              // 功能键只到 F24
    [InlineData("Ctrl+F13")]         // 自定义触发键是单键，不接受组合
    public void IsAllowed_FalseForInvalidNames(string? name)
    {
        Assert.False(CustomTriggerKeys.IsAllowed(name));
    }

    // ==================== Normalize ====================

    [Fact]
    public void Normalize_TrimsAndKeepsOrder()
    {
        var result = CustomTriggerKeys.Normalize(["  F13 ", "CapsLock"]);

        Assert.Equal(["F13", "CapsLock"], result);
    }

    [Fact]
    public void Normalize_DropsReservedInvalidAndEmpty()
    {
        var result = CustomTriggerKeys.Normalize(["Ctrl", "", "  ", "F13", "NoSuchKey"]);

        Assert.Equal(["F13"], result);
    }

    [Fact]
    public void Normalize_DeduplicatesByVirtualKeyNotByName()
    {
        // PageUp 与 PgUp 是同一个键的两个名字 —— 必须只留一个，
        // 否则钩子里会出现两个等价触发键，用户删掉一个另一个还在。
        var result = CustomTriggerKeys.Normalize(["PageUp", "PgUp"]);

        Assert.Single(result);
    }

    [Fact]
    public void Normalize_CapsAtMaxCount()
    {
        var names = new[] { "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10" };

        var result = CustomTriggerKeys.Normalize(names);

        Assert.Equal(CustomTriggerKeys.MaxCount, result.Count);
        Assert.Equal("F1", result[0]);
    }

    [Fact]
    public void Normalize_NullOrEmpty_ReturnsEmptyList()
    {
        Assert.Empty(CustomTriggerKeys.Normalize(null));
        Assert.Empty(CustomTriggerKeys.Normalize([]));
    }

    // ==================== ToVks ====================

    [Fact]
    public void ToVks_ResolvesKnownKeys()
    {
        var vks = CustomTriggerKeys.ToVks(["CapsLock", "F13"]);

        Assert.Equal([0x14, 0x7C], vks);
    }

    /// <summary>
    /// 钩子报告的是左右分离键码（0xA0/0xA1），泛键码 0x10 只有展开后才可能命中 ——
    /// 否则把 Shift 设成触发键等于没设。这条钉住展开逻辑。
    /// </summary>
    [Fact]
    public void ToVks_ExpandsGenericShiftToBothSides()
    {
        var vks = CustomTriggerKeys.ToVks(["Shift"]);

        Assert.Equal([0x10, 0xA0, 0xA1], vks);
    }

    [Fact]
    public void ToVks_ShiftCountsAsOneNameForMaxCap()
    {
        // 名额数的是用户配置的键数，不是展开后的码数：8 个键里 Shift 只占 1 个名额
        var names = new[] { "Shift", "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8" };
        var normalized = CustomTriggerKeys.Normalize(names);

        Assert.Equal(CustomTriggerKeys.MaxCount, normalized.Count);
        Assert.Contains("Shift", normalized);
    }

    [Fact]
    public void ToVks_SkipsInvalidAndDuplicates()
    {
        var vks = CustomTriggerKeys.ToVks(["Ctrl", "F13", "NoSuchKey", "F13"]);

        Assert.Equal([0x7C], vks);
    }

    [Fact]
    public void ToVks_Null_ReturnsEmpty()
    {
        Assert.Empty(CustomTriggerKeys.ToVks(null));
    }

    [Fact]
    public void ToVks_CapsAtMaxCount()
    {
        var names = new[] { "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10" };

        Assert.Equal(CustomTriggerKeys.MaxCount, CustomTriggerKeys.ToVks(names).Length);
    }

    [Fact]
    public void ToVks_DoesNotExpandCtrlAltWin()
    {
        // Ctrl / Alt / Win 有各自的预设分支同时匹配泛码与左右码，展开反而多余；
        // 而且它们本来就是保留名，不该出现在这里
        Assert.Empty(CustomTriggerKeys.ToVks(["Ctrl", "Alt", "Win"]));
    }

    // ==================== Describe ====================

    [Fact]
    public void Describe_JoinsWithPlus()
    {
        Assert.Equal("F13 + CapsLock", CustomTriggerKeys.Describe(["F13", "CapsLock"]));
    }

    [Fact]
    public void Describe_EmptyWhenNothingUsable()
    {
        Assert.Equal("", CustomTriggerKeys.Describe(null));
        Assert.Equal("", CustomTriggerKeys.Describe(["Ctrl", "NoSuchKey"]));
    }

    // ==================== Suggestions ====================

    [Fact]
    public void Suggestions_AreAllAllowed()
    {
        // 下拉框里的推荐项必须都能直接用，否则用户选中后点“添加”就被拒绝
        foreach (var name in CustomTriggerKeys.Suggestions)
            Assert.True(CustomTriggerKeys.IsAllowed(name), $"推荐项 {name} 无法解析");
    }

    [Fact]
    public void Suggestions_HaveNoDuplicates()
    {
        Assert.Equal(CustomTriggerKeys.Suggestions.Length,
            CustomTriggerKeys.Suggestions.Distinct().Count());
    }
}
