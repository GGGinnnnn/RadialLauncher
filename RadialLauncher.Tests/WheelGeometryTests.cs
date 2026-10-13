using RadialLauncher.Models;
using RadialLauncher.Services;
using Xunit;

namespace RadialLauncher.Tests;

/// <summary>
/// WheelGeometry 的纯计算测试。
/// 这是修复"四向轮盘预览被裁剪"的核心：WheelWindow 与 WheelPreview 现在共用这里的系数。
/// </summary>
public class WheelGeometryTests
{
    private static AppearanceSettings Appearance(
        double itemSize = 74,
        double scale = 1.0,
        bool glow = false,
        double deadZone = 34) => new()
    {
        ItemSize = itemSize,
        Scale = scale,
        ShowGlow = glow,
        DeadZone = deadZone,
    };

    // ==================== 方向数收敛 ====================

    [Theory]
    [InlineData(4, 4)]
    [InlineData(8, 8)]
    [InlineData(0, 8)]
    [InlineData(5, 8)]
    [InlineData(6, 8)]
    [InlineData(12, 8)]
    [InlineData(-1, 8)]
    public void NormalizeDirectionCount_OnlyAllowsFourOrEight(int input, int expected)
    {
        Assert.Equal(expected, WheelGeometry.NormalizeDirectionCount(input));
    }

    // ==================== 半径 ====================

    [Fact]
    public void RadiusFor_FourWayIsLargerThanEightWay()
    {
        // 四向选项间隔 90°，需要更大半径才不显得挤 —— 这正是预览裁剪 bug 的根源
        var four = WheelGeometry.RadiusFor(74, 4);
        var eight = WheelGeometry.RadiusFor(74, 8);

        Assert.True(four > eight);
        Assert.Equal(74 * WheelGeometry.RadiusFactor4, four, 6);
        Assert.Equal(74 * WheelGeometry.RadiusFactor8, eight, 6);
    }

    [Fact]
    public void RadiusFor_ScalesWithItemSize()
    {
        var small = WheelGeometry.RadiusFor(50, 8);
        var large = WheelGeometry.RadiusFor(100, 8);

        Assert.Equal(small * 2, large, 6);
    }

    // ==================== 窗口尺寸：四向必须比八向大 ====================

    [Fact]
    public void Compute_FourWayWindowIsLargerThanEightWay()
    {
        // 这是回归护栏：旧代码的预览固定按 1.95 估算 baseWindow，
        // 因此四向轮盘（真实按 2.05）会被裁掉一圈。
        var four = WheelGeometry.Compute(Appearance(), 4);
        var eight = WheelGeometry.Compute(Appearance(), 8);

        Assert.True(four.WindowSize > eight.WindowSize,
            $"四向窗口 {four.WindowSize} 应大于八向 {eight.WindowSize}");
    }

    [Fact]
    public void Compute_WindowSizeEqualsTwoTimesPlateRadiusPlusGlow()
    {
        var g = WheelGeometry.Compute(Appearance(glow: true), 8);

        Assert.Equal(2 * (g.PlateRadius + g.GlowPadding), g.WindowSize, 6);
    }

    [Fact]
    public void Compute_PlateRadiusIncludesItemPadding()
    {
        var g = WheelGeometry.Compute(Appearance(itemSize: 100), 8);

        Assert.Equal(g.Radius + g.ItemSize * WheelGeometry.PlatePaddingFactor, g.PlateRadius, 6);
    }

    // ==================== 光晕留白 ====================

    [Fact]
    public void Compute_GlowPaddingIsZeroWhenDisabled()
    {
        var g = WheelGeometry.Compute(Appearance(glow: false), 8);
        Assert.Equal(0, g.GlowPadding);
    }

    [Fact]
    public void Compute_GlowPaddingIsAppliedWhenEnabled()
    {
        var withGlow = WheelGeometry.Compute(Appearance(glow: true), 8);
        var withoutGlow = WheelGeometry.Compute(Appearance(glow: false), 8);

        Assert.Equal(WheelGeometry.GlowPadding, withGlow.GlowPadding);
        // 光晕预留了额外的窗口空间：windowSize = 2*(plateRadius + glowPadding)
        Assert.True(withGlow.WindowSize > withoutGlow.WindowSize);
        Assert.Equal(withoutGlow.WindowSize + 2 * WheelGeometry.GlowPadding,
            withGlow.WindowSize, 6);
    }

    // ==================== 缩放 ====================

    [Fact]
    public void Compute_GeometryScaleMultipliesItemSize()
    {
        var full = WheelGeometry.Compute(Appearance(itemSize: 80, scale: 1.0), 8);
        var half = WheelGeometry.Compute(Appearance(itemSize: 80, scale: 1.0), 8, geometryScale: 0.5);

        Assert.Equal(full.ItemSize / 2, half.ItemSize, 6);
        Assert.Equal(full.WindowSize / 2, half.WindowSize, 6);
    }

    [Fact]
    public void Compute_AppearanceScaleAndGeometryScaleCombine()
    {
        var g = WheelGeometry.Compute(Appearance(itemSize: 80, scale: 1.2), 8, geometryScale: 0.5);

        Assert.Equal(80 * 1.2 * 0.5, g.ItemSize, 6);
        Assert.Equal(1.2 * 0.5, g.Scale, 6);
    }

    [Fact]
    public void Compute_NormalizedDirectionCountIsReflected()
    {
        var g = WheelGeometry.Compute(Appearance(), 6);
        Assert.Equal(8, g.DirectionCount);
    }

    // ==================== 派生尺寸 ====================

    [Fact]
    public void Compute_InnerDiameterIsFractionOfPlate()
    {
        var g = WheelGeometry.Compute(Appearance(), 8);
        Assert.Equal(g.PlateDiameter * WheelGeometry.InnerRatio, g.InnerDiameter, 6);
    }

    [Fact]
    public void Compute_DeadZoneNeverBelowFloor()
    {
        // 极小 scale 下 DeadZone 也应保底 12，避免整个盘都成了死区
        var g = WheelGeometry.Compute(Appearance(deadZone: 34, scale: 0.01), 8);
        Assert.True(g.DeadZone >= 12);
    }

    [Fact]
    public void Compute_DeadZoneScalesWithItemSize()
    {
        var g = WheelGeometry.Compute(Appearance(deadZone: 100, scale: 1.0), 8);
        Assert.Equal(100, g.DeadZone, 6);
    }

    [Fact]
    public void Compute_FontSizesHaveFloors()
    {
        var g = WheelGeometry.Compute(Appearance(itemSize: 1), 8);

        Assert.True(g.IconFontSize >= 12);
        Assert.True(g.LabelFontSize >= 8);
    }

    [Fact]
    public void Compute_IconDecodeWidthHasFloor()
    {
        var g = WheelGeometry.Compute(Appearance(itemSize: 1), 8);
        Assert.True(g.IconDecodeWidth >= WheelGeometry.MinIconDecodeWidth);
    }

    // ==================== FitScaleFor ====================

    [Fact]
    public void FitScaleFor_NeverExceedsOne()
    {
        // 可用区域很大时不应放大
        var fit = WheelGeometry.FitScaleFor(Appearance(), 8, availableSize: 5000);
        Assert.Equal(1.0, fit);
    }

    [Fact]
    public void FitScaleFor_ShrinksWhenAreaTooSmall()
    {
        var fit = WheelGeometry.FitScaleFor(Appearance(itemSize: 120), 8, availableSize: 100);
        Assert.True(fit < 1.0);
        Assert.True(fit > 0);
    }

    [Fact]
    public void FitScaleFor_ShrunkGeometryFitsWithinAvailableSize()
    {
        const double available = 200;
        var appearance = Appearance(itemSize: 120, glow: true);
        var fit = WheelGeometry.FitScaleFor(appearance, 8, available);

        var g = WheelGeometry.Compute(appearance, 8, fit);

        Assert.True(g.WindowSize <= available,
            $"缩放后窗口 {g.WindowSize} 应能塞进 {available}");
    }

    [Fact]
    public void FitScaleFor_AccountsForFourWayBeingBigger()
    {
        // 同样可用区域下，四向需要的缩放更小（因为它的标准窗口更大）
        const double available = 260;
        var appearance = Appearance(itemSize: 120);

        var fit4 = WheelGeometry.FitScaleFor(appearance, 4, available);
        var fit8 = WheelGeometry.FitScaleFor(appearance, 8, available);

        Assert.True(fit4 <= fit8,
            $"四向 fit {fit4} 应不大于八向 fit {fit8}");
    }

    [Fact]
    public void FitScaleFor_FourWayResultFitsWithinAvailableSize()
    {
        // 回归护栏：旧实现固定按八向估算，四向结果会溢出可用区域
        const double available = 260;
        var appearance = Appearance(itemSize: 120, glow: true);

        var fit = WheelGeometry.FitScaleFor(appearance, 4, available);
        var g = WheelGeometry.Compute(appearance, 4, fit);

        Assert.True(g.WindowSize <= available,
            $"四向缩放后窗口 {g.WindowSize} 应能塞进 {available}");
    }

    [Fact]
    public void FitScaleFor_HandlesDegenerateAvailableSize()
    {
        // 可用区域不足以容纳固定的光晕外框或边距时，退化为 1.0（内容自然溢出），
        // 但不能返回 0 / 负数，也不能抛异常。
        Assert.Equal(1.0, WheelGeometry.FitScaleFor(Appearance(glow: true), 8, availableSize: 0));
        Assert.Equal(1.0, WheelGeometry.FitScaleFor(Appearance(glow: true), 8, availableSize: 16));
        Assert.Equal(1.0, WheelGeometry.FitScaleFor(Appearance(glow: true), 8, availableSize: -50));

        // 无光晕时，正数可用区域应给出合法的正缩放
        Assert.True(WheelGeometry.FitScaleFor(Appearance(glow: false), 8, availableSize: 100) > 0);
    }

    // ==================== 与 WheelWindow 的一致性 ====================

    [Fact]
    public void Compute_MatchesPreviouslyHardcodedWindowFormula()
    {
        // 旧 WheelWindow 的公式：2 * (radius + itemSize*0.72 + glow)
        const double itemSize = 74;
        var g = WheelGeometry.Compute(Appearance(itemSize: itemSize, scale: 1.0, glow: true), 8);

        double expectedRadius = itemSize * 1.95;
        double expected = 2 * (expectedRadius + itemSize * 0.72 + 36);

        Assert.Equal(expected, g.WindowSize, 6);
    }
}
