using System.ComponentModel;
using RadialLauncher.Services;
using Xunit;

namespace RadialLauncher.Tests;

/// <summary>
/// Loc 本地化表的测试。
/// Loc 是进程内单例且有可变语言状态，因此每个测试前后都要把语言复位成 zh-CN。
/// </summary>
public class LocTests : IDisposable
{
    public LocTests()
    {
        Loc.Instance.SetLanguage("zh-CN");
    }

    public void Dispose()
    {
        Loc.Instance.SetLanguage("zh-CN");
    }

    // ==================== 索引器 ====================

    [Fact]
    public void Indexer_ReturnsEmptyForNullOrEmptyKey()
    {
        Assert.Equal("", Loc.Instance[null!]);
        Assert.Equal("", Loc.Instance[""]);
    }

    [Fact]
    public void Indexer_ReturnsChineseValueByDefault()
    {
        Assert.Equal("保存", Loc.Instance["Common_Save"]);
    }

    [Fact]
    public void Indexer_FallsBackToKeyNameForUnknownKey()
    {
        const string unknown = "__NoSuchKey__";
        Assert.Equal(unknown, Loc.Instance[unknown]);
    }

    [Fact]
    public void Indexer_ReturnsEnglishValueAfterSwitchingLanguage()
    {
        Loc.Instance.SetLanguage("en");

        Assert.NotEqual("保存", Loc.Instance["Common_Save"]);
        Assert.False(string.IsNullOrWhiteSpace(Loc.Instance["Common_Save"]));
    }

    // ==================== SetLanguage ====================

    [Theory]
    [InlineData("en")]
    [InlineData("en-US")]
    [InlineData("EN")]
    [InlineData("En-GB")]
    public void SetLanguage_EnglishVariantsAllSwitchToEnglish(string language)
    {
        Loc.Instance.SetLanguage(language);

        Assert.True(Loc.Instance.IsEnglish);
        Assert.StartsWith("en", Loc.Instance.Language, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("zh-CN")]
    [InlineData("fr-FR")]
    [InlineData("de")]
    public void SetLanguage_NonEnglishInputFallsBackToChinese(string? language)
    {
        Loc.Instance.SetLanguage(language!);

        Assert.False(Loc.Instance.IsEnglish);
        Assert.Equal("zh-CN", Loc.Instance.Language);
        Assert.Equal("保存", Loc.Instance["Common_Save"]);
    }

    [Fact]
    public void SetLanguage_TrimsWhitespace()
    {
        Loc.Instance.SetLanguage("  en  ");
        Assert.True(Loc.Instance.IsEnglish);
    }

    // ==================== PropertyChanged ====================

    [Fact]
    public void SetLanguage_RaisesItemIndexerNotification()
    {
        var raised = new List<string?>();
        PropertyChangedEventHandler handler = (_, e) => raised.Add(e.PropertyName);
        Loc.Instance.PropertyChanged += handler;

        try
        {
            Loc.Instance.SetLanguage("en");
        }
        finally
        {
            Loc.Instance.PropertyChanged -= handler;
        }

        // 索引器绑定靠 "Item[]" 刷新，漏掉它界面就不会更新
        Assert.Contains("Item[]", raised);
        Assert.Contains(nameof(Loc.Language), raised);
        Assert.Contains(nameof(Loc.IsEnglish), raised);
    }

    [Fact]
    public void SetLanguage_SameLanguageTwiceRaisesNoExtraNotification()
    {
        Loc.Instance.SetLanguage("zh-CN");   // 先确保已是中文

        var count = 0;
        PropertyChangedEventHandler handler = (_, _) => count++;
        Loc.Instance.PropertyChanged += handler;

        try
        {
            Loc.Instance.SetLanguage("zh-CN");   // 与当前相同，应直接返回
        }
        finally
        {
            Loc.Instance.PropertyChanged -= handler;
        }

        Assert.Equal(0, count);
    }

    // ==================== T / HasKey / Keys / Pick ====================

    [Fact]
    public void T_DelegatesToIndexer()
    {
        Assert.Equal(Loc.Instance["Common_Save"], Loc.T("Common_Save"));
    }

    [Fact]
    public void HasKey_TrueForKnownKey()
    {
        Assert.True(Loc.HasKey("Common_Save"));
        Assert.True(Loc.HasKey("App_Title"));
    }

    [Fact]
    public void HasKey_FalseForUnknownOrEmptyKey()
    {
        Assert.False(Loc.HasKey("__NoSuchKey__"));
        Assert.False(Loc.HasKey(""));
        Assert.False(Loc.HasKey(null!));
    }

    [Fact]
    public void Keys_ReturnsNonEmptyCollectionOfKnownKeys()
    {
        var keys = Loc.Keys;
        Assert.NotEmpty(keys);
        Assert.Contains("Common_Save", keys);
    }

    [Fact]
    public void Pick_ReturnsChineseWhenLanguageIsChinese()
    {
        Loc.Instance.SetLanguage("zh-CN");
        Assert.Equal("中文", Loc.Pick("中文", "English"));
    }

    [Fact]
    public void Pick_ReturnsEnglishWhenLanguageIsEnglish()
    {
        Loc.Instance.SetLanguage("en");
        Assert.Equal("English", Loc.Pick("中文", "English"));
    }

    // ==================== 表完整性 ====================

    [Fact]
    public void AllKeys_MatchUnderscoreNamingConvention()
    {
        // 注释里明确要求：键名只用下划线，不要用点号
        foreach (var key in Loc.Keys)
        {
            Assert.DoesNotContain(".", key);
            Assert.False(string.IsNullOrWhiteSpace(key));
        }
    }

    [Fact]
    public void AllKeys_ReturnNonEmptyTranslation()
    {
        Loc.Instance.SetLanguage("zh-CN");
        foreach (var key in Loc.Keys)
            Assert.False(string.IsNullOrEmpty(Loc.Instance[key]), $"键 {key} 的中文值为空");
    }

    [Fact]
    public void AllKeys_HaveEnglishTranslation()
    {
        // 英文表若缺键，索引器会静默回退到中文 —— 中英混排很难看，所以逐条检查。
        // 先一次性把中文原文快照下来，避免在循环里反复切语言污染状态。
        Loc.Instance.SetLanguage("zh-CN");
        var zhSnapshot = Loc.Keys.ToDictionary(k => k, k => Loc.Instance[k]);

        Loc.Instance.SetLanguage("en");

        var missing = new List<string>();
        foreach (var key in Loc.Keys)
        {
            var en = Loc.Instance[key];
            // 回退发生时返回值等于中文值；借此找出英文表缺失的键
            if (en == zhSnapshot[key]) missing.Add(key);
        }

        Assert.True(missing.Count == 0,
            $"英文表缺少 {missing.Count} 个键的翻译：" + string.Join(", ", missing.Take(20)) +
            (missing.Count > 20 ? " …" : ""));
    }
}
