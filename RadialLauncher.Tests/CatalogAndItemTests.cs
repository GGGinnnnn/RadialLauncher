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
