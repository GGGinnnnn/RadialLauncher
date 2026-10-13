using System.IO;
using RadialLauncher.Services;
using Xunit;

namespace RadialLauncher.Tests;

/// <summary>
/// Logger 的可测部分：级别过滤、消息格式、路径。
///
/// 日志目录已可重定向（见 TestEnvironment）：测试进程统一指到临时目录，
/// 因此这里可以安全地做“改目录 → 落盘 → 读回”的验证，不会再污染用户真实日志。
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
    public void DirectoryPath_IsRedirectable()
    {
        var original = Logger.DirectoryPath;
        var custom = Path.Combine(Path.GetTempPath(), "RadialLauncher.Tests-redirect-" + Guid.NewGuid().ToString("N"));

        try
        {
            Logger.DirectoryPath = custom;

            Assert.Equal(custom, Logger.DirectoryPath);
            Assert.Equal(Path.Combine(custom, "RadialLauncher.log"), Logger.LogPath);
        }
        finally
        {
            Logger.DirectoryPath = original;
        }
    }

    [Fact]
    public void DirectoryPath_RejectsBlankValue()
    {
        var original = Logger.DirectoryPath;

        // 空目录名没有任何意义，必须当场拒绝，而不是让后面写日志时静默失败
        Assert.Throws<ArgumentException>(() => Logger.DirectoryPath = "   ");
        Assert.Equal(original, Logger.DirectoryPath);
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

    // ==================== 异步落盘 ====================

    private static string NewTempDir() => Path.Combine(
        Path.GetTempPath(), "RadialLauncher.Tests-log-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Write_ThenFlush_PersistsToFile()
    {
        var originalDir = Logger.DirectoryPath;
        var originalLevel = Logger.MinLevel;
        var dir = NewTempDir();

        try
        {
            Logger.DirectoryPath = dir;
            Logger.MinLevel = LogLevel.Info;

            Logger.Info("落盘标记-12345");
            Logger.Flush();

            Assert.True(File.Exists(Logger.LogPath));
            Assert.Contains("落盘标记-12345", Logger.ReadTail(50));
        }
        finally
        {
            Logger.DirectoryPath = originalDir;   // 先放掉文件句柄，才能删目录
            Logger.MinLevel = originalLevel;
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void Clear_RemovesLogFile()
    {
        var originalDir = Logger.DirectoryPath;
        var dir = NewTempDir();

        try
        {
            Logger.DirectoryPath = dir;
            Logger.MinLevel = LogLevel.Info;

            Logger.Info("即将被清空");
            Logger.Flush();
            Assert.True(File.Exists(Logger.LogPath));

            Logger.Clear();
            Assert.False(File.Exists(Logger.LogPath));
        }
        finally
        {
            Logger.DirectoryPath = originalDir;
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }
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
