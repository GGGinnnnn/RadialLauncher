using System.Threading;
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

    // ==================== 惰性缓存与并发 ====================

    [Fact]
    public void Cache_ReturnsSameInstanceOnRepeatedAccess()
    {
        FontCatalog.InvalidateCache();

        var first = FontCatalog.InstalledFamilies;
        var second = FontCatalog.InstalledFamilies;

        // 同一份缓存对象，不应每次重建
        Assert.Same(first, second);
    }

    [Fact]
    public void InvalidateCache_ForcesRebuild()
    {
        var first = FontCatalog.InstalledFamilies;
        FontCatalog.InvalidateCache();
        var second = FontCatalog.InstalledFamilies;

        Assert.NotSame(first, second);
        // 内容应保持一致（系统字体没变）
        Assert.Equal(first, second);
    }

    [Fact]
    public void InstalledFamilies_IsSortedAndDistinct()
    {
        FontCatalog.InvalidateCache();
        var list = FontCatalog.InstalledFamilies;

        var sorted = list
            .OrderBy(s => s, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        Assert.Equal(sorted, list);
        Assert.Equal(list.Count, list.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void ConcurrentFirstAccess_DoesNotThrowAndYieldsConsistentCache()
    {
        // 回归护栏：缓存未加锁时，多线程首次访问会各自枚举一遍系统字体，
        // 且 _installedSorted 的非原子赋值可能让别的线程读到半成品。
        // 现在用双检锁 + Volatile 发布，并发下应只产出一份一致的缓存。
        const int threads = 8;
        var results = new List<string>[threads];
        var errors = new List<Exception>();

        var workers = new Thread[threads];
        using var barrier = new Barrier(threads);

        for (int i = 0; i < threads; i++)
        {
            int index = i;
            workers[i] = new Thread(() =>
            {
                try
                {
                    barrier.SignalAndWait();   // 尽量让所有线程同时首次访问
                    results[index] = FontCatalog.InstalledFamilies;

                    // 顺带压一下 InstalledSet 那条路径
                    _ = FontCatalog.IsInstalled("Segoe UI");
                }
                catch (Exception ex)
                {
                    lock (errors) errors.Add(ex);
                }
            });
            workers[i].IsBackground = true;
        }

        FontCatalog.InvalidateCache();
        foreach (var w in workers) w.Start();
        foreach (var w in workers) w.Join();

        Assert.Empty(errors);

        // 所有线程拿到的应是同一个实例
        var first = results[0];
        Assert.NotNull(first);
        foreach (var list in results)
            Assert.Same(first, list);
    }
}
