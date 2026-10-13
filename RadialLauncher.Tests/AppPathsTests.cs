using System;
using System.IO;
using RadialLauncher.Services;
using Xunit;

namespace RadialLauncher.Tests;

/// <summary>
/// AppPaths 的数据目录解析与模式切换。
///
/// 每个用例都把两个根目录（exe 同级、%AppData%）换到独立临时目录，结束再原样还原 ——
/// 既不会碰用户真实的 %AppData%\RadialLauncher，也不会把便携标记写进测试的构建输出目录。
/// 用例会改 <see cref="ConfigService.DataDirectory"/> / <see cref="Logger.DirectoryPath"/>
/// 这两个进程级全局量，所以与同样操作它们的往返测试串到同一个 collection 里跑。
/// </summary>
[Collection("ConfigServiceSerial")]
public sealed class AppPathsTests : IDisposable
{
    private readonly string _root;
    private readonly string _exeDir;
    private readonly string _appDataDir;

    private readonly string _originalDataDir;
    private readonly string _originalLogDir;
    private readonly string _originalExeDir;
    private readonly string _originalAppDataDir;

    public AppPathsTests()
    {
        _originalDataDir = ConfigService.DataDirectory;
        _originalLogDir = Logger.DirectoryPath;
        _originalExeDir = AppPaths.ExecutableDirectory;
        _originalAppDataDir = AppPaths.AppDataDirectory;

        _root = Path.Combine(Path.GetTempPath(), "AppPathsTests-" + Guid.NewGuid().ToString("N"));
        _exeDir = Path.Combine(_root, "portable");
        _appDataDir = Path.Combine(_root, "appdata");
        Directory.CreateDirectory(_exeDir);
        Directory.CreateDirectory(_appDataDir);

        AppPaths.ExecutableDirectory = _exeDir;
        AppPaths.AppDataDirectory = _appDataDir;

        // 默认从“AppData 模式”起步，正好也是产品的默认
        ConfigService.DataDirectory = _appDataDir;
        Logger.DirectoryPath = _appDataDir;
    }

    public void Dispose()
    {
        ConfigService.DataDirectory = _originalDataDir;
        Logger.DirectoryPath = _originalLogDir;
        AppPaths.ExecutableDirectory = _originalExeDir;
        AppPaths.AppDataDirectory = _originalAppDataDir;

        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }

    private string MarkerPath => Path.Combine(_exeDir, AppPaths.MarkerFileName);

    private string AppDataConfig => Path.Combine(_appDataDir, "config.json");

    private string ExeConfig => Path.Combine(_exeDir, "config.json");

    // ==================== 目录解析 ====================

    [Fact]
    public void DirectoryFor_ResolvesBothModes()
    {
        Assert.Equal(_appDataDir, AppPaths.DirectoryFor(DataDirectoryMode.AppData));
        Assert.Equal(_exeDir, AppPaths.DirectoryFor(DataDirectoryMode.Portable));
    }

    [Fact]
    public void ExecutableDirectory_TrimsTrailingSeparator()
    {
        AppPaths.ExecutableDirectory = _exeDir + Path.DirectorySeparatorChar;
        Assert.Equal(_exeDir, AppPaths.ExecutableDirectory);

        AppPaths.AppDataDirectory = _appDataDir + Path.DirectorySeparatorChar;
        Assert.Equal(_appDataDir, AppPaths.AppDataDirectory);
    }

    [Fact]
    public void Mode_WithoutMarker_IsAppData()
    {
        Assert.False(File.Exists(MarkerPath));
        Assert.Equal(DataDirectoryMode.AppData, AppPaths.Mode);
        Assert.False(AppPaths.IsPortable);
    }

    [Fact]
    public void Mode_WithMarker_IsPortable()
    {
        File.WriteAllText(MarkerPath, "");

        Assert.Equal(DataDirectoryMode.Portable, AppPaths.Mode);
        Assert.True(AppPaths.IsPortable);
    }

    [Fact]
    public void ResolveDataDirectory_FollowsMarker()
    {
        Assert.Equal(_appDataDir, AppPaths.ResolveDataDirectory());

        File.WriteAllText(MarkerPath, "");
        Assert.Equal(_exeDir, AppPaths.ResolveDataDirectory());
    }

    [Fact]
    public void ResolveDataDirectory_EnvironmentVariableBeatsMarker()
    {
        File.WriteAllText(MarkerPath, "");   // 标记说便携，环境变量仍应压过它

        var forced = Path.Combine(_root, "forced");
        var original = Environment.GetEnvironmentVariable(AppPaths.DataDirectoryEnvVar);

        Environment.SetEnvironmentVariable(AppPaths.DataDirectoryEnvVar, forced);
        try
        {
            Assert.True(AppPaths.IsEnvironmentOverrideActive);
            Assert.Equal(forced, AppPaths.ResolveDataDirectory());
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.DataDirectoryEnvVar, original);
        }

        Assert.False(AppPaths.IsEnvironmentOverrideActive);
        Assert.Equal(_exeDir, AppPaths.ResolveDataDirectory());   // 回到标记文件说了算
    }

    [Fact]
    public void Setters_RejectBlankValues()
    {
        Assert.Throws<ArgumentException>(() => AppPaths.ExecutableDirectory = "  ");
        Assert.Throws<ArgumentException>(() => AppPaths.AppDataDirectory = "  ");
        Assert.Throws<ArgumentException>(() => AppPaths.ApplyDirectory(""));
    }

    [Fact]
    public void ApplyDirectory_KeepsConfigAndLogTogether()
    {
        var dir = Path.Combine(_root, "both");
        AppPaths.ApplyDirectory(dir);

        // 这是本次重构的核心不变量：配置与日志永远同一个目录
        Assert.Equal(dir, ConfigService.DataDirectory);
        Assert.Equal(dir, Logger.DirectoryPath);

        Assert.Equal(Path.Combine(dir, "config.json"), ConfigService.ConfigPath);
        Assert.Equal(Path.Combine(dir, "RadialLauncher.log"), Logger.LogPath);
    }

    // ==================== 切换模式 ====================

    [Fact]
    public void SwitchTo_Portable_WritesMarkerAndMovesData()
    {
        File.WriteAllText(AppDataConfig, "{\"SchemaVersion\":2}");
        File.WriteAllText(Path.Combine(_appDataDir, "RadialLauncher.log"), "log line");

        var result = AppPaths.SwitchTo(DataDirectoryMode.Portable);

        Assert.True(result.Success, result.Error);
        Assert.False(result.AlreadyInMode);
        Assert.Equal(_exeDir, result.TargetDirectory);
        Assert.Equal(2, result.FilesCopied);

        Assert.True(File.Exists(MarkerPath));
        Assert.True(File.Exists(ExeConfig));

        // 源目录刻意保留：切错了好切回去
        Assert.True(File.Exists(AppDataConfig));

        // 进程内目录也一起切了，否则会出现“配置在一边、日志在另一边”
        Assert.Equal(_exeDir, ConfigService.DataDirectory);
        Assert.Equal(_exeDir, Logger.DirectoryPath);
        Assert.Equal(DataDirectoryMode.Portable, AppPaths.Mode);
    }

    [Fact]
    public void SwitchTo_AppData_RemovesMarkerAndMovesDataBack()
    {
        File.WriteAllText(MarkerPath, "");
        File.WriteAllText(ExeConfig, "{}");
        ConfigService.DataDirectory = _exeDir;
        Logger.DirectoryPath = _exeDir;

        var result = AppPaths.SwitchTo(DataDirectoryMode.AppData);

        Assert.True(result.Success, result.Error);
        Assert.False(File.Exists(MarkerPath));
        Assert.True(File.Exists(AppDataConfig));
        Assert.Equal(_appDataDir, ConfigService.DataDirectory);
        Assert.Equal(DataDirectoryMode.AppData, AppPaths.Mode);
    }

    [Fact]
    public void SwitchTo_SameMode_IsNoOp()
    {
        var result = AppPaths.SwitchTo(DataDirectoryMode.AppData);   // 当前就是 AppData

        Assert.True(result.Success);
        Assert.True(result.AlreadyInMode);
        Assert.Equal(0, result.FilesCopied);
        Assert.False(File.Exists(MarkerPath));
    }

    [Fact]
    public void SwitchTo_BacksUpExistingTargetConfig()
    {
        File.WriteAllText(AppDataConfig, "{\"from\":\"appdata\"}");
        File.WriteAllText(ExeConfig, "{\"from\":\"exe\"}");   // 目标目录里已有一份

        var result = AppPaths.SwitchTo(DataDirectoryMode.Portable);

        Assert.True(result.Success, result.Error);

        // 目标里那份被另存，不会被无声冲掉
        Assert.NotNull(result.PreviousConfigBackup);
        Assert.True(File.Exists(result.PreviousConfigBackup!));
        Assert.Equal("{\"from\":\"exe\"}", File.ReadAllText(result.PreviousConfigBackup!));

        // 新目录里应当是“当前正在用的那份”
        Assert.Equal("{\"from\":\"appdata\"}", File.ReadAllText(ExeConfig));
    }

    [Fact]
    public void SwitchTo_SkipsTemporaryFiles()
    {
        File.WriteAllText(AppDataConfig, "{}");
        File.WriteAllText(AppDataConfig + ".tmp", "写了一半的半成品");

        var result = AppPaths.SwitchTo(DataDirectoryMode.Portable);

        Assert.True(result.Success, result.Error);
        Assert.Equal(1, result.FilesCopied);
        Assert.False(File.Exists(ExeConfig + ".tmp"));
    }

    [Fact]
    public void SwitchTo_FromMissingSourceDirectory_StillSucceeds()
    {
        var neverUsed = Path.Combine(_root, "never-used");
        ConfigService.DataDirectory = neverUsed;   // 目录根本不存在（全新安装）
        Logger.DirectoryPath = neverUsed;

        var result = AppPaths.SwitchTo(DataDirectoryMode.Portable);

        Assert.True(result.Success, result.Error);
        Assert.Equal(0, result.FilesCopied);
        Assert.True(File.Exists(MarkerPath));
    }

    [Fact]
    public void SwitchTo_UnwritableTarget_FailsWithoutChangingAnything()
    {
        // 把目标目录指到一个“已经存在的文件”上，创建目录必然失败
        var blocker = Path.Combine(_root, "blocker");
        File.WriteAllText(blocker, "");
        AppPaths.AppDataDirectory = blocker;

        var result = AppPaths.SwitchTo(DataDirectoryMode.AppData);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);

        // 失败要原样收场：目录没被改、标记没被写
        Assert.Equal(_appDataDir, ConfigService.DataDirectory);
        Assert.False(File.Exists(MarkerPath));
        Assert.Equal(DataDirectoryMode.AppData, AppPaths.Mode);
    }

    [Fact]
    public void SwitchTo_MovesInSeveralFilesAtOnce()
    {
        File.WriteAllText(AppDataConfig, "{}");
        File.WriteAllText(Path.Combine(_appDataDir, "config.backup.json"), "{}");
        File.WriteAllText(Path.Combine(_appDataDir, "config.corrupt-20261013-204100.json"), "坏");
        File.WriteAllText(Path.Combine(_appDataDir, "RadialLauncher.log"), "x");
        File.WriteAllText(Path.Combine(_appDataDir, "app.ico"), "x");

        var result = AppPaths.SwitchTo(DataDirectoryMode.Portable);

        Assert.True(result.Success, result.Error);
        Assert.Equal(5, result.FilesCopied);

        foreach (var name in new[]
                 {
                     "config.json", "config.backup.json",
                     "config.corrupt-20261013-204100.json",
                     "RadialLauncher.log", "app.ico",
                 })
        {
            Assert.True(File.Exists(Path.Combine(_exeDir, name)), name);
        }
    }
}
