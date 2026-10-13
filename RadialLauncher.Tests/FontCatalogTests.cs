using RadialLauncher.Services;
using Xunit;

namespace RadialLauncher.Tests;

/// <summary>FontCatalog 的纯逻辑部分：字体回退链拼接。</summary>
public class FontCatalogTests
{
    [Fact]
    public void BuildFamilyChain_PutsChosenFontFirst()
    {
        var chain = FontCatalog.BuildFamilyChain("Arial");
        Assert.StartsWith("Arial", chain);
    }

    [Fact]
    public void BuildFamilyChain_ChosenFontOverridesItsDuplicateInFallbackTail()
    {
        // Arial 本身就在回退链里，因此去重后它只出现在最前面
        var chain = FontCatalog.BuildFamilyChain("Arial");
        Assert.Equal("Arial, Microsoft YaHei UI, Segoe UI", chain);
    }

    [Fact]
    public void BuildFamilyChain_AppendsFallbackTailForFontNotInTail()
    {
        var chain = FontCatalog.BuildFamilyChain("Consolas");
        Assert.Equal("Consolas, " + FontCatalog.FallbackChain, chain);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildFamilyChain_FallsBackWhenChosenEmpty(string? chosen)
    {
        Assert.Equal(FontCatalog.FallbackChain, FontCatalog.BuildFamilyChain(chosen));
    }

    [Fact]
    public void BuildFamilyChain_DeduplicatesCaseInsensitively()
    {
        // 用户选了 Segoe UI，回退链里也有 Segoe UI，不应出现两次
        var chain = FontCatalog.BuildFamilyChain("Segoe UI");
        var occurrences = chain
            .Split(',')
            .Select(p => p.Trim())
            .Count(p => string.Equals(p, "Segoe UI", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(1, occurrences);
    }

    [Fact]
    public void BuildFamilyChain_HandlesCommaSeparatedUserInput()
    {
        var chain = FontCatalog.BuildFamilyChain("Consolas, Cascadia Code");
        var parts = chain.Split(',').Select(p => p.Trim()).ToList();

        Assert.Equal("Consolas", parts[0]);
        Assert.Equal("Cascadia Code", parts[1]);
    }

    [Fact]
    public void BuildFamilyChain_IgnoresBlankSegmentsInUserInput()
    {
        var chain = FontCatalog.BuildFamilyChain("Consolas,,  ,Cascadia Code");
        var parts = chain.Split(',').Select(p => p.Trim()).ToList();

        Assert.Equal("Consolas", parts[0]);
        Assert.Equal("Cascadia Code", parts[1]);
    }

    [Fact]
    public void BuildFamilyChain_TrimsWhitespaceFromChosenFont()
    {
        var chain = FontCatalog.BuildFamilyChain("   Verdana   ");
        Assert.StartsWith("Verdana", chain);
    }

    [Fact]
    public void BuildFamilyChain_ResultIsAlwaysNonEmpty()
    {
        Assert.False(string.IsNullOrWhiteSpace(FontCatalog.BuildFamilyChain(null)));
        Assert.False(string.IsNullOrWhiteSpace(FontCatalog.BuildFamilyChain("")));
    }

    // ==================== Resolve ====================

    [Fact]
    public void Resolve_ReturnsUsableFontFamilyForUnknownFont()
    {
        // 未安装的字体不应抛异常，WPF 会沿回退链继续找
        var family = FontCatalog.Resolve("__NoSuchFont__");
        Assert.NotNull(family);
        Assert.False(string.IsNullOrWhiteSpace(family.Source));
    }

    [Fact]
    public void Resolve_ReturnsUsableFontFamilyForNull()
    {
        var family = FontCatalog.Resolve(null);
        Assert.NotNull(family);
    }

    // ==================== FontOption.Display ====================

    private static FontOption MakeOption(bool installed) => new()
    {
        Family = "TestFamily",
        DisplayZh = "测试字体",
        DisplayEn = "Test Font",
        Installed = installed,
    };

    [Fact]
    public void FontOption_DisplayReturnsPlainNameWhenInstalled()
    {
        Assert.Equal("测试字体", MakeOption(true).Display(english: false));
        Assert.Equal("Test Font", MakeOption(true).Display(english: true));
    }

    [Fact]
    public void FontOption_DisplayAppendsNotInstalledSuffix()
    {
        // Display 的“未安装”后缀走 Loc.Pick，依赖全局语言状态，所以要显式设置
        Loc.Instance.SetLanguage("zh-CN");
        Assert.Contains("未安装", MakeOption(false).Display(english: false));

        Loc.Instance.SetLanguage("en");
        Assert.Contains("not installed", MakeOption(false).Display(english: true));

        Loc.Instance.SetLanguage("zh-CN");
    }

    [Fact]
    public void FontOption_DisplayFallsBackToFamilyWhenNameMissing()
    {
        var option = new FontOption { Family = "FallbackName", DisplayZh = "", DisplayEn = "", Installed = true };
        Assert.Equal("FallbackName", option.Display(english: false));
    }

    // ==================== Recommendations ====================

    [Fact]
    public void Recommendations_ReturnsNonEmptyListWithKnownEntries()
    {
        var list = FontCatalog.Recommendations();

        Assert.NotEmpty(list);
        Assert.Contains(list, o => o.Family == "Microsoft YaHei UI");
        Assert.Contains(list, o => o.Family == "Segoe UI");
    }

    [Fact]
    public void Recommendations_AllEntriesHaveFamilyName()
    {
        foreach (var option in FontCatalog.Recommendations())
            Assert.False(string.IsNullOrWhiteSpace(option.Family));
    }

    [Fact]
    public void IsInstalled_FalseForBlankOrUnknownFont()
    {
        Assert.False(FontCatalog.IsInstalled(""));
        Assert.False(FontCatalog.IsInstalled("   "));
        Assert.False(FontCatalog.IsInstalled("__NoSuchFont__"));
    }

    [Fact]
    public void DescribeEffective_ReturnsNonEmptyStringAlways()
    {
        Assert.False(string.IsNullOrWhiteSpace(FontCatalog.DescribeEffective(null)));
        Assert.False(string.IsNullOrWhiteSpace(FontCatalog.DescribeEffective("__NoSuchFont__")));
    }
}
