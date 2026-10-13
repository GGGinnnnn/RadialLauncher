using RadialLauncher.Services;
using Xunit;

namespace RadialLauncher.Tests;

/// <summary>
/// ShortcutCatalog 的查找 / 规范化 / 搜索逻辑。
/// 注意：这是只读的静态目录，测试不应修改它。
/// </summary>
public class ShortcutCatalogTests
{
    // ==================== Normalize ====================

    [Theory]
    [InlineData("Ctrl+Shift+Esc", "ctrl+esc+shift")]
    [InlineData("ctrl+shift+esc", "ctrl+esc+shift")]
    [InlineData("  Ctrl + E  ", "ctrl+e")]
    [InlineData("E+Ctrl", "ctrl+e")]
    public void Normalize_LowercasesSortsAndStripsSpaces(string input, string expected)
    {
        Assert.Equal(expected, ShortcutCatalog.Normalize(input));
    }

    [Fact]
    public void Normalize_EmptyInputReturnsEmpty()
    {
        Assert.Equal("", ShortcutCatalog.Normalize(""));
    }

    [Fact]
    public void Normalize_IgnoresEmptySegments()
    {
        Assert.Equal("ctrl+e", ShortcutCatalog.Normalize("Ctrl++E"));
    }

    [Fact]
    public void Normalize_IsOrderInsensitive()
    {
        // 这是 ByKeys 能做无序匹配的基础
        Assert.Equal(
            ShortcutCatalog.Normalize("Shift+Ctrl+E"),
            ShortcutCatalog.Normalize("Ctrl+Shift+E"));
    }

    // ==================== ById ====================

    [Fact]
    public void ById_FindsFirstItem()
    {
        var first = ShortcutCatalog.Items[0];
        Assert.Same(first, ShortcutCatalog.ById(first.Id));
    }

    [Fact]
    public void ById_IsCaseInsensitive()
    {
        var first = ShortcutCatalog.Items[0];
        Assert.Same(first, ShortcutCatalog.ById(first.Id.ToUpperInvariant()));
    }

    [Fact]
    public void ById_ReturnsNullForUnknownId()
    {
        Assert.Null(ShortcutCatalog.ById("__definitely-not-a-real-id__"));
    }

    // ==================== ByKeys ====================

    [Fact]
    public void ByKeys_FindsItemIgnoringOrderAndCase()
    {
        var item = ShortcutCatalog.Items[0];
        var found = ShortcutCatalog.ByKeys(item.Keys.ToUpperInvariant());
        Assert.NotNull(found);
        Assert.Equal(item.Id, found!.Id);
    }

    [Fact]
    public void ByKeys_ReturnsNullForEmptyInput()
    {
        Assert.Null(ShortcutCatalog.ByKeys(""));
        Assert.Null(ShortcutCatalog.ByKeys("   "));
        Assert.Null(ShortcutCatalog.ByKeys(null!));
    }

    [Fact]
    public void ByKeys_ReturnsNullWhenNoMatch()
    {
        Assert.Null(ShortcutCatalog.ByKeys("__no-such-shortcut__"));
    }

    // ==================== Categories ====================

    [Fact]
    public void Categories_ReturnsNonEmptyDistinctList()
    {
        var categories = ShortcutCatalog.Categories(english: false);
        Assert.NotEmpty(categories);
        Assert.Equal(categories.Count, categories.Distinct().Count());
    }

    [Fact]
    public void Categories_PreservesFirstAppearanceOrder()
    {
        var categories = ShortcutCatalog.Categories(english: false);
        var expected = new List<string>();
        foreach (var item in ShortcutCatalog.Items)
            if (!expected.Contains(item.CategoryZh)) expected.Add(item.CategoryZh);

        Assert.Equal(expected, categories);
    }

    [Fact]
    public void Categories_EnglishVariantWorks()
    {
        var categories = ShortcutCatalog.Categories(english: true);
        Assert.NotEmpty(categories);
        Assert.Equal(categories.Count, categories.Distinct().Count());
    }

    // ==================== DisplayName / CategoryName ====================

    [Fact]
    public void DisplayName_SwitchesByLanguageFlag()
    {
        var item = ShortcutCatalog.Items[0];
        Assert.Equal(item.NameZh, ShortcutCatalog.DisplayName(item, english: false));
        Assert.Equal(item.NameEn, ShortcutCatalog.DisplayName(item, english: true));
    }

    [Fact]
    public void CategoryName_SwitchesByLanguageFlag()
    {
        var item = ShortcutCatalog.Items[0];
        Assert.Equal(item.CategoryZh, ShortcutCatalog.CategoryName(item, english: false));
        Assert.Equal(item.CategoryEn, ShortcutCatalog.CategoryName(item, english: true));
    }

    // ==================== Search ====================

    [Fact]
    public void Search_EmptyQueryReturnsEverything()
    {
        Assert.Equal(ShortcutCatalog.Items.Count, ShortcutCatalog.Search("", false).Count);
        Assert.Equal(ShortcutCatalog.Items.Count, ShortcutCatalog.Search("   ", false).Count);
        Assert.Equal(ShortcutCatalog.Items.Count, ShortcutCatalog.Search(null, false).Count);
    }

    [Fact]
    public void Search_MatchesByName()
    {
        var first = ShortcutCatalog.Items[0];
        var results = ShortcutCatalog.Search(first.NameZh, english: false);
        Assert.Contains(first, results);
    }

    [Fact]
    public void Search_IsCaseInsensitive()
    {
        var first = ShortcutCatalog.Items[0];
        if (first.NameEn.Length >= 3)
        {
            var upper = first.NameEn.ToUpperInvariant();
            Assert.Contains(first, ShortcutCatalog.Search(upper, english: false));
        }
    }

    [Fact]
    public void Search_MatchesByKeys()
    {
        var first = ShortcutCatalog.Items[0];
        var results = ShortcutCatalog.Search(first.Keys, english: false);
        Assert.Contains(first, results);
    }

    [Fact]
    public void Search_ReturnsEmptyForNonsenseQuery()
    {
        Assert.Empty(ShortcutCatalog.Search("__zzz_no_such_thing_zzz__", false));
    }

    [Fact]
    public void Search_TrimsQuery()
    {
        var first = ShortcutCatalog.Items[0];
        var trimmed = first.NameZh.Trim();
        if (trimmed.Length > 0)
            Assert.Contains(first, ShortcutCatalog.Search("  " + trimmed + "  ", false));
    }

    // ==================== Grouped ====================

    [Fact]
    public void Grouped_KeysCoverAllItems()
    {
        var groups = ShortcutCatalog.Grouped(english: false);
        Assert.Equal(ShortcutCatalog.Items.Count, groups.Sum(g => g.Count()));
    }

    // ==================== IsValidKeys ====================

    [Fact]
    public void IsValidKeys_TrueForParsableShortcut()
    {
        Assert.True(ShortcutCatalog.IsValidKeys("Ctrl+Shift+Esc", out var error));
        Assert.Equal("", error);
    }

    [Fact]
    public void IsValidKeys_FalseForGarbage()
    {
        Assert.False(ShortcutCatalog.IsValidKeys("NotAKey", out var error));
        Assert.NotEqual("", error);
    }

    // ==================== 数据完整性 ====================

    [Fact]
    public void AllItems_HaveRequiredFields()
    {
        foreach (var item in ShortcutCatalog.Items)
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Id), "Id 不能为空");
            Assert.False(string.IsNullOrWhiteSpace(item.NameZh), $"{item.Id} 缺少中文名");
            Assert.False(string.IsNullOrWhiteSpace(item.NameEn), $"{item.Id} 缺少英文名");
            Assert.False(string.IsNullOrWhiteSpace(item.CategoryZh), $"{item.Id} 缺少中文分类");
            Assert.False(string.IsNullOrWhiteSpace(item.CategoryEn), $"{item.Id} 缺少英文分类");
            Assert.False(string.IsNullOrWhiteSpace(item.Keys), $"{item.Id} 缺少按键");
        }
    }

    [Fact]
    public void AllItems_HaveUniqueIds()
    {
        var ids = ShortcutCatalog.Items.Select(i => i.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void AllItems_KeysAreParsable()
    {
        // 目录里每一条都必须能被合成器解析，否则执行时会静默失败
        foreach (var item in ShortcutCatalog.Items)
        {
            Assert.True(
                ShortcutCatalog.IsValidKeys(item.Keys, out var error),
                $"{item.Id} 的按键「{item.Keys}」无法解析：{error}");
        }
    }

    [Fact]
    public void ByKeys_IsDeterministicForDuplicateKeySequences()
    {
        // 目录里同一组按键可能出现在多个分类下（例如 Ctrl+N 在资源管理器与浏览器里含义不同），
        // 这是参考资料的性质决定的，不是数据错误。
        // 但 ByKeys 只返回第一条 —— 需求侧要清楚：回显名称可能与用户点选的那条不一致。
        var grouped = ShortcutCatalog.Items
            .GroupBy(i => ShortcutCatalog.Normalize(i.Keys))
            .Where(g => g.Count() > 1)
            .ToList();

        foreach (var group in grouped)
        {
            var first = group.First();
            var found = ShortcutCatalog.ByKeys(first.Keys);

            Assert.NotNull(found);
            Assert.Equal(first.Id, found!.Id);
        }
    }

    [Fact]
    public void DuplicateKeySequences_AreReportedForReview()
    {
        // 不强制唯一（目录设计允许重复），但把重复情况打印出来便于人工核对，
        // 并断言重复条目里每一条自身都是合法可解析的。
        var duplicates = ShortcutCatalog.Items
            .GroupBy(i => ShortcutCatalog.Normalize(i.Keys))
            .Where(g => g.Count() > 1)
            .ToList();

        foreach (var group in duplicates)
        {
            foreach (var item in group)
            {
                Assert.True(
                    ShortcutCatalog.IsValidKeys(item.Keys, out var error),
                    $"{item.Id} 的按键「{item.Keys}」无法解析：{error}");
            }
        }
    }
}
