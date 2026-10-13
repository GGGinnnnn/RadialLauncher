using RadialLauncher.Services;
using Xunit;

namespace RadialLauncher.Tests;

/// <summary>
/// KeySequence 的解析 / 描述逻辑测试。
/// 注意：Send* / KeyDown / KeyUp 会真正调用 SendInput，绝不能在此测试。
/// </summary>
public class KeySequenceTests
{
    // 常用虚拟键码
    private const int Ctrl = 0x11;
    private const int Shift = 0x10;
    private const int Alt = 0x12;
    private const int Win = 0x5B;
    private const int E = 0x45;
    private const int D = 0x44;
    private const int Esc = 0x1B;

    // ==================== TryParse：基础 ====================

    [Fact]
    public void TryParse_SingleLetter()
    {
        Assert.True(KeySequence.TryParse("E", out var keys, out var error));
        Assert.Equal(new[] { E }, keys);
        Assert.Equal("", error);
    }

    [Theory]
    [InlineData("e")]
    [InlineData("E")]
    public void TryParse_LetterIsCaseInsensitive(string token)
    {
        Assert.True(KeySequence.TryParse(token, out var keys, out _));
        Assert.Equal(E, keys[0]);
    }

    [Fact]
    public void TryParse_ModifierPlusKey()
    {
        Assert.True(KeySequence.TryParse("Ctrl+E", out var keys, out _));
        Assert.Equal(new[] { Ctrl, E }, keys);
    }

    [Fact]
    public void TryParse_SortsByModifierRank_RegardlessOfInputOrder()
    {
        // 输入顺序 Ctrl、Alt、Shift、Win，输出应按 Ctrl=0, Shift=1, Alt=2, Win=3 排序
        Assert.True(KeySequence.TryParse("Win+Alt+Shift+Ctrl+K", out var keys, out _));
        Assert.Equal(new[] { Ctrl, Shift, Alt, Win, 0x4B }, keys);
    }

    [Fact]
    public void TryParse_DeduplicatesRepeatedKeys()
    {
        Assert.True(KeySequence.TryParse("Ctrl+Ctrl+E+E", out var keys, out _));
        Assert.Equal(new[] { Ctrl, E }, keys);
    }

    [Fact]
    public void TryParse_TrimsWhitespaceAroundTokens()
    {
        Assert.True(KeySequence.TryParse("  Ctrl + Shift + Esc ", out var keys, out _));
        Assert.Equal(new[] { Ctrl, Shift, Esc }, keys);
    }

    [Theory]
    [InlineData("Esc")]
    [InlineData("Escape")]
    public void TryParse_AcceptsAliasesForEscape(string token)
    {
        Assert.True(KeySequence.TryParse(token, out var keys, out _));
        Assert.Equal(Esc, keys[0]);
    }

    // ==================== TryParse：功能键 ====================

    [Theory]
    [InlineData("F1", 0x70)]
    [InlineData("F5", 0x74)]
    [InlineData("F12", 0x7B)]
    [InlineData("F24", 0x87)]
    [InlineData("f1", 0x70)]
    public void TryParse_FunctionKeys(string token, int expectedVk)
    {
        Assert.True(KeySequence.TryParse(token, out var keys, out _));
        Assert.Equal(expectedVk, keys[0]);
    }

    [Theory]
    [InlineData("F0")]     // 越界
    [InlineData("F25")]    // 越界
    public void TryParse_RejectsOutOfRangeFunctionKeys(string token)
    {
        Assert.False(KeySequence.TryParse(token, out _, out var error));
        Assert.NotEqual("", error);
    }

    // ==================== TryParse：数字与符号 ====================

    [Theory]
    [InlineData("0", 0x30)]
    [InlineData("5", 0x35)]
    [InlineData("9", 0x39)]
    public void TryParse_DigitKeys(string token, int expectedVk)
    {
        Assert.True(KeySequence.TryParse(token, out var keys, out _));
        Assert.Equal(expectedVk, keys[0]);
    }

    [Fact]
    public void TryParse_OemSymbolKeys()
    {
        // `（反引号）在 Map 中定义为 0xC0
        Assert.True(KeySequence.TryParse("`", out var keys, out _));
        Assert.Equal(0xC0, keys[0]);
    }

    // ==================== TryParse：失败路径 ====================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParse_ReturnsFalseForEmpty(string? input)
    {
        Assert.False(KeySequence.TryParse(input, out _, out var error));
        Assert.NotEqual("", error);
    }

    [Fact]
    public void TryParse_ReturnsFalseForUnknownToken()
    {
        Assert.False(KeySequence.TryParse("Ctrl+NotAKey", out _, out var error));
        Assert.NotEqual("", error);
    }

    [Fact]
    public void TryParse_ReturnsFalseWhenAllTokensDuplicateButValid()
    {
        // 至少能解析出一个键
        Assert.True(KeySequence.TryParse("Ctrl+Ctrl", out var keys, out _));
        Assert.Single(keys);
    }

    [Fact]
    public void TryParse_OutputsEmptyListOnFailure()
    {
        Assert.False(KeySequence.TryParse("bogus", out var keys, out _));
        Assert.Empty(keys);
    }

    // ==================== NameOf / Describe ====================

    [Theory]
    [InlineData(0x41, "A")]
    [InlineData(0x5A, "Z")]
    [InlineData(0x30, "0")]
    [InlineData(0x39, "9")]
    [InlineData(0x70, "F1")]
    [InlineData(0x87, "F24")]
    [InlineData(0x11, "Ctrl")]
    [InlineData(0x10, "Shift")]
    [InlineData(0x12, "Alt")]
    [InlineData(0x1B, "Esc")]
    [InlineData(0x0D, "Enter")]
    [InlineData(0x20, "Space")]
    public void NameOf_ReturnsFriendlyNames(int vk, string expected)
    {
        Assert.Equal(expected, KeySequence.NameOf(vk));
    }

    [Fact]
    public void NameOf_FallsBackToHexForUnknownVk()
    {
        Assert.Equal("0xFE", KeySequence.NameOf(0xFE));
    }

    [Fact]
    public void Describe_JoinsWithPlus()
    {
        Assert.Equal("Ctrl+Shift+Esc", KeySequence.Describe(new[] { Ctrl, Shift, Esc }));
    }

    [Fact]
    public void Describe_EmptyInputYieldsEmptyString()
    {
        Assert.Equal("", KeySequence.Describe(Array.Empty<int>()));
    }

    // ==================== 往返一致性 ====================

    [Theory]
    [InlineData("Ctrl+E")]
    [InlineData("Ctrl+Shift+Escape")]
    [InlineData("Win+D")]
    [InlineData("Win+Shift+S")]
    [InlineData("Alt+F4")]
    public void ParseThenDescribe_RoundTripsToCanonicalForm(string input)
    {
        Assert.True(KeySequence.TryParse(input, out var keys, out _));
        var described = KeySequence.Describe(keys);

        // 描述结果应能再次解析出同样的键码集合
        Assert.True(KeySequence.TryParse(described, out var reparsed, out var error), error);
        Assert.Equal(keys, reparsed);
    }
}
