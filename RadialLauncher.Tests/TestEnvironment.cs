using System.IO;
using System.Runtime.CompilerServices;
using RadialLauncher.Services;

namespace RadialLauncher.Tests;

/// <summary>
/// 测试进程级环境准备（在程序集加载时执行一次）。
///
/// 被测程序集（RadialLauncher）默认把配置和日志写到 %AppData%\RadialLauncher，
/// 测试若直接跑，就会读写用户**真实**的 config.json 与日志：
///   · 用例中途中断时 Dispose 不执行，用户配置会停在测试内容上甚至被删掉；
///   · 测试产生的 Info/Warn 日志会混进用户日志，排障时严重误导。
/// 因此这里把两个目录一起重定向到临时目录，从根上隔离。
/// </summary>
internal static class TestEnvironment
{
    /// <summary>本次测试运行专用的临时数据目录（位于系统临时目录下）。</summary>
    public static string DataDirectory { get; } = Path.Combine(
        Path.GetTempPath(),
        "RadialLauncher.Tests-" + Guid.NewGuid().ToString("N"));

    [ModuleInitializer]
    internal static void Initialize()
    {
        Directory.CreateDirectory(DataDirectory);

        // 配置与日志都改到临时目录，绝不碰用户的 %AppData%\RadialLauncher
        ConfigService.DataDirectory = DataDirectory;
        Logger.DirectoryPath = DataDirectory;

        // 测试制造的 Info/Warn 日志没有保留价值，只留错误
        Logger.MinLevel = LogLevel.Error;
    }
}
