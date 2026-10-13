using System.Windows.Media;
using RadialLauncher.Services;
using Xunit;

namespace RadialLauncher.Tests;

/// <summary>BrushHelper 的纯逻辑测试：十六进制解析、格式化、明暗判定。</summary>
public class BrushHelperTests
{
    // ==================== TryParseColor ====================

    [Theory]
    [InlineData("#FF0000", 0xFF, 0xFF, 0x00, 0x00)]
    [InlineData("#ff0000", 0xFF, 0xFF, 0x00, 0x00)]
    [InlineData("FF0000", 0xFF, 0xFF, 0x00, 0x00)]      // 不带 # 也能解析
    [InlineData("#80FF0000", 0x80, 0xFF, 0x00, 0x00)]    // 8 位视为 ARGB
    [InlineData("#FF4A9EFF", 0xFF, 0x4A, 0x9E, 0xFF)]
    public void TryParseColor_ParsesRgbAndArgb(string input, byte a, byte r, byte g, byte b)
    {
        Assert.True(BrushHelper.TryParseColor(input, out var color));
        Assert.Equal(a, color.A);
        Assert.Equal(r, color.R);
        Assert.Equal(g, color.G);
        Assert.Equal(b, color.B);
    }

    [Theory]
    [InlineData("#F00", 0xFF, 0xFF, 0x00, 0x00)]   // 3 位缩写 → 每位重复
    [InlineData("#0F0", 0xFF, 0x00, 0xFF, 0x00)]
    [InlineData("ABC", 0xFF, 0xAA, 0xBB, 0xCC)]
    public void TryParseColor_ExpandsThreeDigitShorthand(string input, byte a, byte r, byte g, byte b)
    {
        Assert.True(BrushHelper.TryParseColor(input, out var color));
        Assert.Equal(a, color.A);
        Assert.Equal(r, color.R);
        Assert.Equal(g, color.G);
        Assert.Equal(b, color.B);
    }

    [Fact]
    public void TryParseColor_SixDigitAlwaysOpaque()
    {
        Assert.True(BrushHelper.TryParseColor("#123456", out var color));
        Assert.Equal(0xFF, color.A);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("#12345")]      // 5 位非法
    [InlineData("#1234567")]    // 7 位非法
    [InlineData("#GGGGGG")]     // 非十六进制
    [InlineData("#")]           // 只有井号
    public void TryParseColor_RejectsInvalidInput(string? input)
    {
        Assert.False(BrushHelper.TryParseColor(input, out _));
    }

    [Fact]
    public void TryParseColor_TrimsWhitespace()
    {
        Assert.True(BrushHelper.TryParseColor("  #FF0000  ", out var color));
        Assert.Equal(Colors.Red, color);
    }

    [Fact]
    public void ParseColor_ReturnsFallbackForInvalidInput()
    {
        Assert.Equal(Colors.Blue, BrushHelper.ParseColor("not-a-color", Colors.Blue));
    }

    [Fact]
    public void ParseColor_ReturnsParsedValueWhenValid()
    {
        Assert.Equal(Colors.Lime, BrushHelper.ParseColor("#00FF00", Colors.Blue));
    }

    // ==================== ToHex / ToHexNoAlpha ====================

    [Theory]
    [InlineData(0x00, 0x00, 0x00, 0x00, "#00000000")]
    [InlineData(0xFF, 0xFF, 0xFF, 0xFF, "#FFFFFFFF")]
    [InlineData(0x80, 0x4A, 0x9E, 0xFF, "#804A9EFF")]
    public void ToHex_ProducesEightDigitArgb(byte a, byte r, byte g, byte b, string expected)
    {
        Assert.Equal(expected, BrushHelper.ToHex(Color.FromArgb(a, r, g, b)));
    }

    [Fact]
    public void ToHexNoAlpha_DropsAlphaChannel()
    {
        Assert.Equal("#4A9EFF", BrushHelper.ToHexNoAlpha(Color.FromArgb(0x00, 0x4A, 0x9E, 0xFF)));
    }

    [Fact]
    public void ToHex_RoundTripsThroughTryParseColor()
    {
        var original = Color.FromArgb(0x12, 0x34, 0x56, 0x78);
        var hex = BrushHelper.ToHex(original);

        Assert.True(BrushHelper.TryParseColor(hex, out var parsed));
        Assert.Equal(original, parsed);
    }

    // ==================== IsLight ====================

    [Theory]
    [InlineData(0xFF, 0xFF, 0xFF)]   // 白
    [InlineData(0xFF, 0xFF, 0x00)]   // 亮黄
    [InlineData(0xD9, 0xD9, 0xD9)]   // 浅灰
    public void IsLight_TrueForBrightColors(byte r, byte g, byte b)
    {
        Assert.True(BrushHelper.IsLight(Color.FromRgb(r, g, b)));
    }

    [Theory]
    [InlineData(0x00, 0x00, 0x00)]   // 黑
    [InlineData(0x1A, 0x1A, 0x2E)]   // 深蓝灰
    [InlineData(0x00, 0x00, 0xFF)]   // 纯蓝（加权亮度不足）
    public void IsLight_FalseForDarkColors(byte r, byte g, byte b)
    {
        Assert.False(BrushHelper.IsLight(Color.FromRgb(r, g, b)));
    }

    [Fact]
    public void IsLight_IgnoresAlphaChannel()
    {
        // 全透明但 RGB 是白色，仍应判为亮色
        Assert.True(BrushHelper.IsLight(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF)));
    }
}
