using RadialLauncher.Services;
using Xunit;

namespace RadialLauncher.Tests;

/// <summary>
/// Logger 的可测部分：级别过滤、消息格式、事件。
///
/// 注意：Logger 的日志路径是写死的 %AppData%\RadialLauncher，无法重定向，
/// 因此这里刻意只测“被 MinLevel 拦下、不触盘”的路径。
/// 真正写文件的路径会污染用户的真实 AppData，不适合放进单元测试。
/// 这也是一个可测试性缺口 —— 若日后把路径改成可注入，就能补上落盘测试。
/// </summary>
public class LoggerTests : IDisposable
{
    private readonly LogLevel _originalLevel = Logger.MinLevel;

    public LoggerTests()
    {
        Logger.MinLevel = LogLevel.Info;
    }

    public void Dispose()
    {
        Logger.MinLevel = _originalLevel;
    }

    // ==================== 级别过滤 ====================

    [Fact]
    public void MinLevel_FiltersBelowThreshold()
    {
        Logger.MinLevel = LogLevel.Error;

        var raised = 0;
        Action<string> handler = _ => raised++;
        Logger.EntryWritten += handler;

        try
        {
            Logger.Debug("debug");
            Logger.Info("info");
            Logger.Warn("warn");

            // 三条都低于 Error，应被拦下，且不触发事件
            Assert.Equal(0, raised);
        }
        finally
        {
            Logger.EntryWritten -= handler;
        }
    }

    [Fact]
    public void MinLevel_ComparisonMatchesSeverityOrdering()
    {
        // 过滤是 `level < MinLevel` 直接比较枚举值，所以枚举的数值顺序必须正确
        Assert.True(LogLevel.Debug < LogLevel.Info);
        Assert.True(LogLevel.Info < LogLevel.Warn);
        Assert.True(LogLevel.Warn < LogLevel.Error);
    }

    [Fact]
    public void MinLevel_IsSettable()
    {
        Logger.MinLevel = LogLevel.Warn;
        Assert.Equal(LogLevel.Warn, Logger.MinLevel);

        Logger.MinLevel = LogLevel.Error;
        Assert.Equal(LogLevel.Error, Logger.MinLevel);
    }

    // ==================== 日志路径 ====================

    [Fact]
    public void DirectoryPath_IsUnderAppData()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        Assert.StartsWith(appData, Logger.DirectoryPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LogPath_IsInsideDirectory()
    {
        Assert.StartsWith(Logger.DirectoryPath, Logger.LogPath, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(".log", Logger.LogPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LogPath_UsesExpectedFileName()
    {
        Assert.EndsWith("RadialLauncher.log", Logger.LogPath, StringComparison.OrdinalIgnoreCase);
    }

    // ==================== 枚举语义 ====================

    [Fact]
    public void LogLevel_HasExpectedOrdering()
    {
        Assert.Equal(0, (int)LogLevel.Debug);
        Assert.Equal(1, (int)LogLevel.Info);
        Assert.Equal(2, (int)LogLevel.Warn);
        Assert.Equal(3, (int)LogLevel.Error);
    }
}
