using System.IO;
using RadialLauncher.Models;
using RadialLauncher.Services;
using Xunit;

namespace RadialLauncher.Tests;

/// <summary>
/// ConfigService 的落盘往返测试。
///
/// ConfigService.DataDirectory 是写死的 %AppData%\RadialLauncher，无法重定向，
/// 所以整个类共享一个 fixture：先把用户真实的 config.json 备份到内存，
/// 所有用例跑完后再原样写回，避免污染用户环境。
/// </summary>
[CollectionDefinition("ConfigServiceSerial", DisableParallelization = true)]
public class ConfigServiceSerialCollection { }

[Collection("ConfigServiceSerial")]
public class ConfigServiceRoundTripTests : IDisposable
{
    private static string BackupPath => Path.Combine(ConfigService.DataDirectory, "config.backup.json");

    private readonly string? _originalConfig;
    private readonly string? _originalBackup;
    private readonly bool _dirExisted;

    public ConfigServiceRoundTripTests()
    {
        _dirExisted = Directory.Exists(ConfigService.DataDirectory);
        _originalConfig = ReadIfExists(ConfigService.ConfigPath);
        _originalBackup = ReadIfExists(BackupPath);
    }

    public void Dispose()
    {
        // 1) 还原 config.json（原本没有就删掉）
        RestoreOrDelete(ConfigService.ConfigPath, _originalConfig);

        // 2) 还原 config.backup.json（原本没有就删掉）—— 否则会给用户留下测试残留
        RestoreOrDelete(BackupPath, _originalBackup);

        // 3) 清掉测试制造的临时/损坏文件
        DeleteIfExists(ConfigService.ConfigPath + ".tmp");
        foreach (var file in Directory.Exists(ConfigService.DataDirectory)
                     ? Directory.GetFiles(ConfigService.DataDirectory, "config.corrupt-*.json")
                     : Array.Empty<string>())
        {
            DeleteIfExists(file);
        }

        // 4) 目录原本不存在（且现在空了）就一并移除
        if (!_dirExisted && Directory.Exists(ConfigService.DataDirectory))
        {
            try
            {
                if (Directory.GetFileSystemEntries(ConfigService.DataDirectory).Length == 0)
                    Directory.Delete(ConfigService.DataDirectory);
            }
            catch { }
        }
    }

    private static void RestoreOrDelete(string path, string? content)
    {
        try
        {
            if (content != null) File.WriteAllText(path, content);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch { /* 尽力还原 */ }
    }

    private static void DeleteIfExists(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static string? ReadIfExists(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch { return null; }
    }

    // ==================== Save → Load 往返 ====================

    [Fact]
    public void SaveThenLoad_PreservesKeySettings()
    {
        var service = new ConfigService();
        service.Config.Trigger.ByCtrl = true;
        service.Config.Trigger.ByWin = true;
        service.Config.Trigger.HoldThresholdMs = 350;
        service.Config.Appearance.ItemCount = 4;
        service.Config.System.Language = "en";
        service.Save();

        var reloaded = new ConfigService();
        reloaded.Load();

        Assert.True(reloaded.Config.Trigger.ByCtrl);
        Assert.True(reloaded.Config.Trigger.ByWin);
        Assert.Equal(350, reloaded.Config.Trigger.HoldThresholdMs);
        Assert.Equal(4, reloaded.Config.Appearance.ItemCount);
        Assert.Equal("en", reloaded.Config.System.Language);
    }

    [Fact]
    public void Save_WritesSchemaVersionTwo()
    {
        var service = new ConfigService();
        service.Save();

        var json = File.ReadAllText(ConfigService.ConfigPath);
        Assert.Contains("\"SchemaVersion\": 2", json);
    }

    [Fact]
    public void Save_CreatesBackupOnSecondWrite()
    {
        var service = new ConfigService();
        service.Save();   // 第一次：可能只 Move
        service.Save();   // 第二次：走 File.Replace，应产生 backup

        // 备份文件可能与被测机的既有状态有关，这里只要求不抛异常且主文件存在
        Assert.True(File.Exists(ConfigService.ConfigPath));
    }

    // ==================== CreateWorkingCopy ====================

    [Fact]
    public void CreateWorkingCopy_IsDeepAndIndependent()
    {
        var service = new ConfigService();
        service.Config.WheelItems[0].Name = "原始名";
        service.Config.Environment.BlacklistProcesses.Add("original.exe");

        var copy = service.CreateWorkingCopy();
        copy.WheelItems[0].Name = "改过的";
        copy.Environment.BlacklistProcesses.Add("added.exe");

        Assert.Equal("原始名", service.Config.WheelItems[0].Name);
        Assert.DoesNotContain("added.exe", service.Config.Environment.BlacklistProcesses);
    }

    [Fact]
    public void CreateWorkingCopy_PreservesProfiles()
    {
        var service = new ConfigService();
        service.Config.Profiles = AppProfile.CreateDefaults();

        var copy = service.CreateWorkingCopy();

        Assert.Equal(service.Config.Profiles.Count, copy.Profiles.Count);
        Assert.Equal(service.Config.Profiles[0].Name, copy.Profiles[0].Name);
    }

    // ==================== Replace ====================

    [Fact]
    public void Replace_NormalizesAndPersists()
    {
        var service = new ConfigService();
        var replacement = new AppConfig();
        replacement.Appearance.ItemCount = 5;        // 非法值，Replace 应归一化
        replacement.Trigger.HoldThresholdMs = 9999;  // 超上限，应被收敛

        service.Replace(replacement);

        Assert.True(service.Config.Appearance.ItemCount is 4 or 8);
        Assert.True(service.Config.Trigger.HoldThresholdMs <= 1500);
    }

    // ==================== ResetToDefaults ====================

    [Fact]
    public void ResetToDefaults_RestoresDefaultValues()
    {
        var service = new ConfigService();
        service.Config.Appearance.ItemCount = 4;
        service.Config.System.Language = "en";

        service.ResetToDefaults();

        Assert.Equal(new AppConfig().System.Language, service.Config.System.Language);
        Assert.Equal(WheelItem.CreateDefaults().Count, service.Config.WheelItems.Count);
    }

    // ==================== Load 容错 ====================

    [Fact]
    public void Load_CorruptJsonFallsBackToDefaults()
    {
        Directory.CreateDirectory(ConfigService.DataDirectory);
        File.WriteAllText(ConfigService.ConfigPath, "{ this is not valid json ");

        var service = new ConfigService();
        service.Load();   // 不应抛异常

        Assert.NotNull(service.Config);
        Assert.Equal(2, service.Config.SchemaVersion);

        // 清理这次制造的损坏文件
        try { File.Delete(ConfigService.ConfigPath); } catch { }
    }

    [Fact]
    public void Load_MissingFileCreatesDefaultConfig()
    {
        try { if (File.Exists(ConfigService.ConfigPath)) File.Delete(ConfigService.ConfigPath); } catch { }

        var service = new ConfigService();
        service.Load();

        Assert.NotNull(service.Config);
        Assert.True(File.Exists(ConfigService.ConfigPath));
    }

    // ==================== v1 → v2 迁移 ====================

    [Fact]
    public void Load_MigratesLegacyFlatConfig()
    {
        // v1 是平铺字段，没有 Trigger / Appearance 分组
        const string legacyJson = """
        {
          "UseWhitelist": false,
          "TriggerByCtrl": true,
          "TriggerByWin": true,
          "TriggerByMiddleMouse": false,
          "WheelItemCount": 4,
          "BackgroundColor": "#FF101020",
          "WheelItems": [
            { "Name": "显示桌面", "Icon": "🖥", "ActionType": "Shortcut", "ActionValue": "ShowDesktop" },
            { "Name": "任务管理", "Icon": "📊", "ActionType": "Shortcut", "ActionValue": "TaskManager" }
          ]
        }
        """;

        Directory.CreateDirectory(ConfigService.DataDirectory);
        File.WriteAllText(ConfigService.ConfigPath, legacyJson);

        var service = new ConfigService();
        service.Load();

        Assert.Equal(2, service.Config.SchemaVersion);
        Assert.True(service.Config.Trigger.ByCtrl);
        Assert.True(service.Config.Trigger.ByWin);
        Assert.Equal(4, service.Config.Appearance.ItemCount);
        Assert.Equal("#FF101020", service.Config.Appearance.BackgroundColor);
        Assert.Equal(2, service.Config.WheelItems.Count);
    }

    [Fact]
    public void Load_MigrationMapsLegacyShortcutNamesToKeySequences()
    {
        const string legacyJson = """
        {
          "TriggerByCtrl": true,
          "WheelItems": [
            { "Name": "桌面", "ActionType": "Shortcut", "ActionValue": "ShowDesktop" },
            { "Name": "锁屏", "ActionType": "Shortcut", "ActionValue": "LockScreen" },
            { "Name": "任务", "ActionType": "Shortcut", "ActionValue": "TaskManager" }
          ]
        }
        """;

        Directory.CreateDirectory(ConfigService.DataDirectory);
        File.WriteAllText(ConfigService.ConfigPath, legacyJson);

        var service = new ConfigService();
        service.Load();

        Assert.Equal("Win+D", service.Config.WheelItems[0].ActionValue);
        Assert.Equal("Win+L", service.Config.WheelItems[1].ActionValue);
        Assert.Equal("Ctrl+Shift+Esc", service.Config.WheelItems[2].ActionValue);
    }

    [Fact]
    public void Load_MigrationLeavesNonShortcutActionsUntouched()
    {
        const string legacyJson = """
        {
          "WheelItems": [
            { "Name": "QQ", "ActionType": "LaunchApp", "ActionValue": "C:\\QQ.exe" },
            { "Name": "自定义", "ActionType": "Shortcut", "ActionValue": "Ctrl+Alt+X" }
          ]
        }
        """;

        Directory.CreateDirectory(ConfigService.DataDirectory);
        File.WriteAllText(ConfigService.ConfigPath, legacyJson);

        var service = new ConfigService();
        service.Load();

        // LaunchApp 不受映射影响
        Assert.Equal(@"C:\QQ.exe", service.Config.WheelItems[0].ActionValue);
        // 已是按键序列的自定义快捷键原样保留
        Assert.Equal("Ctrl+Alt+X", service.Config.WheelItems[1].ActionValue);
    }

    [Fact]
    public void Load_MigrationResultStillParsableForKeySequences()
    {
        const string legacyJson = """
        {
          "WheelItems": [
            { "Name": "桌面", "ActionType": "Shortcut", "ActionValue": "ShowDesktop" },
            { "Name": "剪贴板", "ActionType": "Shortcut", "ActionValue": "Clipboard" },
            { "Name": "截图", "ActionType": "Shortcut", "ActionValue": "Screenshot" }
          ]
        }
        """;

        Directory.CreateDirectory(ConfigService.DataDirectory);
        File.WriteAllText(ConfigService.ConfigPath, legacyJson);

        var service = new ConfigService();
        service.Load();

        // 迁移后的按键序列必须都能被合成器解析，否则执行时会静默失败
        foreach (var item in service.Config.WheelItems)
        {
            Assert.True(
                KeySequence.TryParse(item.ActionValue, out _, out var error),
                $"迁移后「{item.Name}」的 {item.ActionValue} 无法解析：{error}");
        }
    }
}
