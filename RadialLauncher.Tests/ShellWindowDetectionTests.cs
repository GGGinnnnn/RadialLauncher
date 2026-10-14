using RadialLauncher.Services;
using Xunit;

namespace RadialLauncher.Tests;

/// <summary>
/// ShellWindowDetection —— “这个前台窗口是不是 Windows shell 表面”的纯判定。
///
/// 这段判定原先埋在 WindowDetectorService.IsShellWindow 里，是个 private 实例方法、
/// 项目也没有 InternalsVisibleTo，一行测试都写不了。踩的坑是：桌面（Progman / WorkerW）、
/// 壁纸层、任务栏（Shell_TrayWnd / Shell_SecondaryTrayWnd）<b>全部由 explorer.exe 承载</b>，
/// 只比进程名的话它们和真正的资源管理器窗口毫无区别 —— 于是在桌面上唤出的轮盘变成了
/// 「资源管理器」的文件夹专用轮盘（Alt+D / Ctrl+E / Delete 在桌面上全是无效动作）。
///
/// 这里把「哪些类名算 shell、哪些不算」钉死，尤其是 CabinetWClass 必须不算。
/// </summary>
public class ShellWindowDetectionTests : IDisposable
{
    /// <summary>Loc 是进程内单例，DisplayName 的用例会切语言，用完必须复位。</summary>
    public void Dispose() => Loc.Instance.SetLanguage("zh-CN");

    // ==================== shell 表面 ====================

    [Theory]
    [InlineData("Progman", ShellSurface.Desktop)]                 // 桌面（常态）
    [InlineData("WorkerW", ShellSurface.Desktop)]                 // 桌面（壁纸幻灯片 / 动态壁纸）
    [InlineData("Shell_TrayWnd", ShellSurface.Taskbar)]           // 主任务栏
    [InlineData("Shell_SecondaryTrayWnd", ShellSurface.Taskbar)]  // 扩展屏任务栏
    public void Classify_RecognisesShellSurfaces(string className, ShellSurface expected)
    {
        Assert.Equal(expected, ShellWindowDetection.Classify("explorer", className));
    }

    [Fact]
    public void Classify_DesktopClass_IgnoresOwningProcess()
    {
        // 动态壁纸（Wallpaper Engine 之类）会让别的进程去创建 WorkerW，
        // 但它照样是桌面的一部分，不该被当成普通应用窗口。
        Assert.Equal(ShellSurface.Desktop, ShellWindowDetection.Classify("wallpaper32", "WorkerW"));
    }

    [Fact]
    public void Classify_ExplorerShellChrome_IsNotAnAppWindow()
    {
        // 通知区浮出层这类窗口既没有标题，也不该命中任何应用专属配置
        Assert.Equal(ShellSurface.ShellChrome,
            ShellWindowDetection.Classify("explorer", "Shell_NotifyIconOverflowWindow"));
    }

    [Fact]
    public void Classify_ShellPrefixedClass_OfOtherProcess_IsNotShell()
    {
        // Shell_ 前缀只在 explorer 名下才算 shell，免得误伤碰巧这么命名的第三方窗口
        Assert.Equal(ShellSurface.None,
            ShellWindowDetection.Classify("someapp", "Shell_NotifyIconOverflowWindow"));
    }

    [Fact]
    public void Classify_MatchesProcessNameCaseInsensitively()
    {
        Assert.Equal(ShellSurface.ShellChrome,
            ShellWindowDetection.Classify("Explorer", "Shell_NotifyIconOverflowWindow"));
    }

    // ==================== 回归点：真资源管理器不能被误判 ====================

    [Theory]
    [InlineData("explorer", "CabinetWClass")]   // Vista 之后的资源管理器窗口
    [InlineData("explorer", "ExploreWClass")]   // 更老的类名
    [InlineData("explorer", "")]                // 类名读不到时的退路
    public void Classify_RealExplorerWindow_IsNotShell(string process, string className)
    {
        Assert.Equal(ShellSurface.None, ShellWindowDetection.Classify(process, className));
    }

    [Theory]
    [InlineData("chrome", "Chrome_WidgetWin_1")]
    [InlineData("code", "Chrome_WidgetWin_1")]
    [InlineData("devenv", "HwndWrapper[devenv.exe;;guid]")]
    public void Classify_OrdinaryAppWindow_IsNotShell(string process, string className)
    {
        Assert.Equal(ShellSurface.None, ShellWindowDetection.Classify(process, className));
    }

    [Fact]
    public void Classify_NullOrEmptyClassName_IsNotShell()
    {
        Assert.Equal(ShellSurface.None, ShellWindowDetection.Classify("explorer", null));
        Assert.Equal(ShellSurface.None, ShellWindowDetection.Classify("explorer", ""));
        Assert.Equal(ShellSurface.None, ShellWindowDetection.Classify(null, null));
    }

    // ==================== IsShell 必须与 Classify 一致 ====================

    [Theory]
    [InlineData("explorer", "Progman")]
    [InlineData("explorer", "Shell_TrayWnd")]
    [InlineData("explorer", "CabinetWClass")]
    [InlineData("chrome", "Chrome_WidgetWin_1")]
    public void IsShell_AgreesWithClassify(string process, string className)
    {
        Assert.Equal(
            ShellWindowDetection.Classify(process, className) != ShellSurface.None,
            ShellWindowDetection.IsShell(process, className));
    }

    // ==================== 界面展示名 ====================

    [Theory]
    [InlineData(ShellSurface.Desktop, "桌面", "Desktop")]
    [InlineData(ShellSurface.Taskbar, "任务栏", "Taskbar")]
    public void DisplayName_UsesLocalisedNameForShellSurfaces(
        ShellSurface shell, string zh, string en)
    {
        var info = new ForegroundWindowInfo { ProcessName = "explorer", Shell = shell };

        Loc.Instance.SetLanguage("zh-CN");
        Assert.Equal(zh, info.DisplayName);

        Loc.Instance.SetLanguage("en");
        Assert.Equal(en, info.DisplayName);
    }

    [Fact]
    public void DisplayName_FallsBackToRawDisplayForOrdinaryWindows()
    {
        // 桌面 / 任务栏之外的窗口保持原样：界面和日志都还能看到进程名与标题
        var info = new ForegroundWindowInfo
        {
            ProcessName = "explorer",
            Title = "下载",
            ClassName = "CabinetWClass",
            Shell = ShellWindowDetection.Classify("explorer", "CabinetWClass"),
        };

        Assert.Equal("explorer - 下载", info.DisplayName);
        Assert.Equal("explorer - 下载", info.Display);
    }

    [Fact]
    public void IsShell_IsDerivedFromShellKind()
    {
        // IsShell 没有独立 setter，只能由 Shell 推导 —— 不可能出现互相矛盾的快照
        Assert.False(new ForegroundWindowInfo { Shell = ShellSurface.None }.IsShell);
        Assert.True(new ForegroundWindowInfo { Shell = ShellSurface.Desktop }.IsShell);
        Assert.True(new ForegroundWindowInfo { Shell = ShellSurface.Taskbar }.IsShell);
        Assert.True(new ForegroundWindowInfo { Shell = ShellSurface.ShellChrome }.IsShell);
    }
}
