using RadialLauncher.Models;
using RadialLauncher.Services;
using Xunit;

namespace RadialLauncher.Tests;

/// <summary>WheelItem 的克隆与默认值。</summary>
public class WheelItemTests
{
    [Fact]
    public void Clone_ProducesIndependentInstance()
    {
        var original = new WheelItem
        {
            Name = "原项",
            Icon = "★",
            ActionType = "Command",
            ActionValue = "echo hi",
            Arguments = "--flag",
            WorkingDirectory = @"C:\temp",
            RunAsAdmin = true,
            HiddenWindow = true,
        };

        var clone = original.Clone();
        clone.Name = "改过的";
        clone.Arguments = "--other";

        Assert.Equal("原项", original.Name);
        Assert.Equal("--flag", original.Arguments);
        Assert.NotSame(original, clone);
    }

    [Fact]
    public void Clone_CopiesAllFields()
    {
        var original = new WheelItem
        {
            Name = "N",
            Icon = "I",
            IconPath = @"C:\i.png",
            ActionType = "Script",
            ActionValue = @"C:\a.ps1",
            Arguments = "arg",
            WorkingDirectory = @"C:\wd",
            RunAsAdmin = true,
            HiddenWindow = true,
        };

        var clone = original.Clone();

        Assert.Equal(original.Name, clone.Name);
        Assert.Equal(original.Icon, clone.Icon);
        Assert.Equal(original.IconPath, clone.IconPath);
        Assert.Equal(original.ActionType, clone.ActionType);
        Assert.Equal(original.ActionValue, clone.ActionValue);
        Assert.Equal(original.Arguments, clone.Arguments);
        Assert.Equal(original.WorkingDirectory, clone.WorkingDirectory);
        Assert.Equal(original.RunAsAdmin, clone.RunAsAdmin);
        Assert.Equal(original.HiddenWindow, clone.HiddenWindow);
    }

    [Fact]
    public void Defaults_AreSaneForNewItem()
    {
        var item = new WheelItem();

        Assert.Equal("", item.Name);
        Assert.Equal("●", item.Icon);
        Assert.Equal("", item.IconPath);
        Assert.Equal("Shortcut", item.ActionType);
        Assert.False(item.RunAsAdmin);
        Assert.False(item.HiddenWindow);
    }

    [Fact]
    public void CreateDefaults_ReturnsEightItems()
    {
        Assert.Equal(8, WheelItem.CreateDefaults().Count);
    }

    [Fact]
    public void CreateDefaults_AllItemsHaveNameIconAndActionType()
    {
        foreach (var item in WheelItem.CreateDefaults())
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Name));
            Assert.False(string.IsNullOrWhiteSpace(item.Icon));
            Assert.False(string.IsNullOrWhiteSpace(item.ActionType));
        }
    }

    [Fact]
    public void CreateDefaults_ShortcutItemsHaveParsableSequences()
    {
        foreach (var item in WheelItem.CreateDefaults().Where(i => i.ActionType == "Shortcut"))
        {
            Assert.True(
                KeySequence.TryParse(item.ActionValue, out _, out var error),
                $"{item.Name} 的快捷键「{item.ActionValue}」无法解析：{error}");
        }
    }

    [Fact]
    public void CreateDefaults_ReturnsFreshListEachCall()
    {
        var first = WheelItem.CreateDefaults();
        var second = WheelItem.CreateDefaults();

        // 修改一份不应影响另一份
        first[0].Name = "被改了";
        Assert.NotEqual("被改了", second[0].Name);
    }
}

/// <summary>BuiltinCatalog 的查找与分类。</summary>
public class BuiltinCatalogTests
{
    public BuiltinCatalogTests() => Loc.Instance.SetLanguage("zh-CN");

    [Fact]
    public void Items_AreNotEmptyAndComplete()
    {
        Assert.NotEmpty(BuiltinCatalog.Items);

        foreach (var item in BuiltinCatalog.Items)
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Id));
            Assert.False(string.IsNullOrWhiteSpace(item.NameZh));
            Assert.False(string.IsNullOrWhiteSpace(item.NameEn));
            Assert.False(string.IsNullOrWhiteSpace(item.CategoryZh));
            Assert.False(string.IsNullOrWhiteSpace(item.CategoryEn));
        }
    }

    [Fact]
    public void Items_HaveUniqueIds()
    {
        var ids = BuiltinCatalog.Items.Select(i => i.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void ById_FindsKnownEntry()
    {
        var def = BuiltinCatalog.ById("LockScreen");
        Assert.NotNull(def);
        Assert.Equal("LockScreen", def!.Id);
    }

    [Fact]
    public void ById_ReturnsNullForUnknown()
    {
        Assert.Null(BuiltinCatalog.ById("__NoSuchBuiltin__"));
    }

    [Fact]
    public void DisplayName_ChineseByDefault()
    {
        Loc.Instance.SetLanguage("zh-CN");
        Assert.Equal("锁定屏幕", BuiltinCatalog.DisplayName("LockScreen"));
    }

    [Fact]
    public void DisplayName_EnglishAfterSwitching()
    {
        Loc.Instance.SetLanguage("en");
        Assert.Equal("Lock screen", BuiltinCatalog.DisplayName("LockScreen"));
        Loc.Instance.SetLanguage("zh-CN");
    }

    [Fact]
    public void DisplayName_ReturnsIdForUnknownEntry()
    {
        Assert.Equal("__UnknownId__", BuiltinCatalog.DisplayName("__UnknownId__"));
    }

    [Fact]
    public void DisplayName_ReturnsNonePlaceholderForEmptyId()
    {
        Assert.Equal(Loc.T("Common_None"), BuiltinCatalog.DisplayName(""));
    }

    [Fact]
    public void Categories_PreservesOrderAndIsDistinct()
    {
        Loc.Instance.SetLanguage("zh-CN");
        var categories = BuiltinCatalog.Categories();

        Assert.NotEmpty(categories);
        Assert.Equal(categories.Count, categories.Distinct().Count());
        Assert.Equal("系统", categories[0]);

        var expected = new List<string>();
        foreach (var item in BuiltinCatalog.Items)
            if (!expected.Contains(item.CategoryZh)) expected.Add(item.CategoryZh);

        Assert.Equal(expected, categories);
    }

    // ==================== Keys（id → 按键序列的唯一事实来源）====================

    [Fact]
    public void AllDefinedKeys_AreParsableByKeySequence()
    {
        // 回归护栏：以前 ActionExecutor 把内部 id（如 "VolumeUp"）当按键文本传给 KeySequence，
        // 隐式依赖 Map 里有同名键。现在按键序列集中在目录里定义，必须逐条可解析。
        foreach (var item in BuiltinCatalog.Items)
        {
            if (item.Keys == null) continue;

            Assert.True(
                KeySequence.TryParse(item.Keys, out _, out var error),
                $"内置功能「{item.Id}」的按键「{item.Keys}」无法解析：{error}");
        }
    }

    [Fact]
    public void KeysFor_ReturnsKeysForShortcutBackedEntries()
    {
        Assert.Equal("Win+D", BuiltinCatalog.KeysFor("ShowDesktop"));
        Assert.Equal("Win+E", BuiltinCatalog.KeysFor("OpenExplorer"));
        Assert.Equal("VolumeUp", BuiltinCatalog.KeysFor("VolumeUp"));
        Assert.Equal("PlayPause", BuiltinCatalog.KeysFor("PlayPause"));
        Assert.Equal("Win+Shift+S", BuiltinCatalog.KeysFor("Screenshot"));
    }

    [Fact]
    public void KeysFor_ReturnsNullForProcessBackedEntries()
    {
        // 这些功能由程序直接启进程 / 调 API，不走合成快捷键
        Assert.Null(BuiltinCatalog.KeysFor("LockScreen"));
        Assert.Null(BuiltinCatalog.KeysFor("Shutdown"));
        Assert.Null(BuiltinCatalog.KeysFor("OpenTerminal"));
        Assert.Null(BuiltinCatalog.KeysFor("OpenDataFolder"));
    }

    [Fact]
    public void KeysFor_ReturnsNullForUnknownId()
    {
        Assert.Null(BuiltinCatalog.KeysFor("__NoSuchBuiltin__"));
    }

    [Fact]
    public void AllMediaEntries_HaveKeysDefined()
    {
        // 媒体键必须全部在目录里配好，否则 ActionExecutor 会静默失败
        string[] mediaIds =
        {
            "PlayPause", "NextTrack", "PrevTrack", "StopMedia",
            "VolumeUp", "VolumeDown", "Mute",
        };

        foreach (var id in mediaIds)
        {
            var def = BuiltinCatalog.ById(id);
            Assert.NotNull(def);
            Assert.False(string.IsNullOrWhiteSpace(def!.Keys), $"媒体功能 {id} 缺少 Keys");
        }
    }
}

/// <summary>IconService.GuessExecutable 的路径推断逻辑。</summary>
public class IconServiceTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GuessExecutable_ReturnsEmptyForBlankInput(string? input)
    {
        Assert.Equal("", IconService.GuessExecutable(input));
    }

    [Fact]
    public void GuessExecutable_ReturnsEmptyForNonExistentPath()
    {
        Assert.Equal("", IconService.GuessExecutable(@"C:\__no_such_file__.exe"));
    }

    [Fact]
    public void GuessExecutable_FindsExistingFile()
    {
        // 用当前测试程序集自身当真实存在的文件
        var path = typeof(IconServiceTests).Assembly.Location;
        Assert.Equal(path, IconService.GuessExecutable(path));
    }

    [Fact]
    public void GuessExecutable_StripsSurroundingQuotes()
    {
        var path = typeof(IconServiceTests).Assembly.Location;
        Assert.Equal(path, IconService.GuessExecutable($"\"{path}\""));
    }

    [Fact]
    public void GuessExecutable_HandlesPathWithTrailingArguments()
    {
        // 真实存在的文件 + 不存在的参数，应只返回文件部分
        var path = typeof(IconServiceTests).Assembly.Location;
        Assert.Equal(path, IconService.GuessExecutable($"{path} --some-arg"));
    }

    [Fact]
    public void GuessExecutable_ExpandsEnvironmentVariables()
    {
        // %WINDIR% 一定存在；展开后的路径也应能识别
        var expanded = Environment.ExpandEnvironmentVariables("%WINDIR%");
        var guess = IconService.GuessExecutable("%WINDIR%\\System32\\notepad.exe");

        // 文件存在则返回展开后的完整路径，否则返回空 —— 两者都不是原始带 % 的字符串
        Assert.DoesNotContain("%", guess);
        if (guess.Length > 0)
            Assert.StartsWith(expanded, guess, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>IconService 图标缓存的容量上限与命中行为。</summary>
public class IconServiceCacheTests
{
    // 说明：图标提取依赖 shell32 与 WPF 成像，测试环境不一定能成功，
    // 但"失败也缓存 null"与"LRU 淘汰"这两条逻辑与图标是否取到无关，
    // 所以下面都只断言缓存规模，不假设图标一定非空。

    private static string SomePath(int index) => $@"C:\__icon_cache_test__\{index}.exe";

    [Fact]
    public void ClearCache_EmptiesTheCache()
    {
        IconService.GetIcon(SomePath(1));
        Assert.True(IconService.CacheCount > 0);

        IconService.ClearCache();
        Assert.Equal(0, IconService.CacheCount);
    }

    [Fact]
    public void NegativeResultsAreCached()
    {
        // 路径不存在 → 图标为 null，但这个"没图标"的结论必须被缓存，
        // 否则每次经过设置面板都会重新问一遍系统。
        IconService.ClearCache();
        var path = SomePath(42);

        var first = IconService.GetIcon(path);
        Assert.True(IconService.CacheCount > 0);

        var second = IconService.GetIcon(path);
        Assert.Same(first, second);
        Assert.Equal(1, IconService.CacheCount);
    }

    [Fact]
    public void Cache_DoesNotGrowBeyondCapacity()
    {
        // 回归护栏：缓存原本是无上限 Dictionary，设置面板长驻 +
        // 用户反复切换大量图标路径 → 无限增长（每个条目还持有 BitmapSource）。
        IconService.ClearCache();

        for (int i = 0; i < 400; i++)
            IconService.GetIcon(SomePath(i));

        Assert.True(
            IconService.CacheCount <= 256,
            $"缓存条目数 {IconService.CacheCount} 超过上限 256");
    }

    [Fact]
    public void Cache_KeepsRecentlyUsedEntries()
    {
        IconService.ClearCache();

        // 填满并略超上限，确保发生淘汰
        for (int i = 0; i < 400; i++)
            IconService.GetIcon(SomePath(i));

        // 最近访问的那批应当还在缓存里
        var recent = SomePath(399);
        int before = IconService.CacheCount;
        IconService.GetIcon(recent);
        Assert.Equal(before, IconService.CacheCount);   // 命中，不新增
    }

    [Fact]
    public void Cache_SeparatesLargeAndSmallIcons()
    {
        IconService.ClearCache();
        var path = SomePath(7);

        IconService.GetIcon(path, small: false);
        IconService.GetIcon(path, small: true);

        // 大图标与单图标是两条不同的键，应各占一条
        Assert.Equal(2, IconService.CacheCount);
    }

    [Fact]
    public void GetIcon_ReturnsNullForBlankPath()
    {
        IconService.ClearCache();
        Assert.Null(IconService.GetIcon(null));
        Assert.Null(IconService.GetIcon(""));
        Assert.Null(IconService.GetIcon("   "));
        Assert.Equal(0, IconService.CacheCount);   // 空路径不入缓存
    }

    [Fact]
    public void ConcurrentAccess_DoesNotThrow()
    {
        IconService.ClearCache();
        var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        var threads = new System.Threading.Thread[4];
        for (int i = 0; i < threads.Length; i++)
        {
            int offset = i;
            threads[i] = new System.Threading.Thread(() =>
            {
                try
                {
                    for (int n = 0; n < 300; n++)
                        IconService.GetIcon(SomePath(offset * 1000 + n));
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                }
            });
            threads[i].IsBackground = true;
        }

        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();

        Assert.Empty(errors);
        Assert.True(IconService.CacheCount <= 256);
    }
}
