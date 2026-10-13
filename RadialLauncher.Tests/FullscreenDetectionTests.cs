using RadialLauncher.Services;
using Xunit;

namespace RadialLauncher.Tests;

/// <summary>
/// FullscreenDetection —— “前台窗口算不算全屏”的纯几何判定。
///
/// 这段判定原先整个埋在 WindowDetectorService.CheckFullscreen 的 Win32 调用中间，
/// 一行测试都没有，结果踩了一个很隐蔽的坑：任务栏自动隐藏时工作区（rcWork）与显示器区域
/// （rcMonitor）完全重合，而 Windows 10/11 的最大化窗口会向外溢出版面约 7px，
/// 于是<b>普通最大化窗口被判成“全屏”</b>；默认开启的 SkipFullscreenApps 再把它拦掉 ——
/// 现象是“按住 Ctrl 没反应，日志里也干干净净”，排查时只能靠真机做对照实验反推。
///
/// 这里把“最大化不算全屏”和四条边的覆盖条件全部钉死。
/// </summary>
public class FullscreenDetectionTests
{
    // ==================== 测试脚手架 ====================

    private static ScreenRect R(int left, int top, int right, int bottom) => new(left, top, right, bottom);

    /// <summary>本机实测：任务栏自动隐藏，显示器区域与工作区重合。</summary>
    private static readonly ScreenRect Monitor = R(0, 0, 1707, 960);

    /// <summary>本机实测：最大化窗口会向外溢出版面 7px。</summary>
    private static readonly ScreenRect MaximizedWithBleed = R(-7, -7, 1714, 968);

    // ==================== 回归点：最大化 ≠ 全屏 ====================

    [Fact]
    public void MaximizedWindow_BleedingPastMonitor_IsNotFullscreen()
    {
        // 就是那个 bug：矩形明明“覆盖了整个显示器”，但它只是个最大化的窗口。
        Assert.False(FullscreenDetection.IsFullscreen(MaximizedWithBleed, Monitor, isMaximized: true));
    }

    [Fact]
    public void MaximizedWindow_ExactlyMatchingMonitor_IsNotFullscreen()
    {
        // 别指望“不漏出去”就能救：任务栏自动隐藏时，最大化窗口的矩形就等于显示器区域。
        Assert.False(FullscreenDetection.IsFullscreen(Monitor, Monitor, isMaximized: true));
    }

    [Fact]
    public void MaximizedWindow_WithVisibleTaskbar_IsNotFullscreen()
    {
        // 任务栏可见时工作区比显示器矮 40px，光比矩形就已经不覆盖了 —— 双重保险。
        var window = R(0, 0, 1707, 920);
        Assert.False(FullscreenDetection.IsFullscreen(window, Monitor, isMaximized: true));
    }

    [Fact]
    public void OnlyMaximizedFlagDiffers_DecidesTheOutcome()
    {
        // 同一份矩形，只差 isMaximized 一位 —— 把“它是唯一判别依据”写进断言。
        Assert.True(FullscreenDetection.IsFullscreen(Monitor, Monitor, isMaximized: false));
        Assert.False(FullscreenDetection.IsFullscreen(Monitor, Monitor, isMaximized: true));
    }

    // ==================== 真全屏不能被误伤 ====================

    [Fact]
    public void BorderlessFullscreen_ExactlyMatchingMonitor_IsFullscreen()
    {
        Assert.True(FullscreenDetection.IsFullscreen(Monitor, Monitor, isMaximized: false));
    }

    [Fact]
    public void BorderlessFullscreen_SlightlyLargerThanMonitor_IsFullscreen()
    {
        // 全屏窗口为了盖住自己的边框常会比显示器略大一圈，
        // 所以判定必须用 <= / >= 而不是 ==，否则会把真全屏漏掉。
        Assert.True(FullscreenDetection.IsFullscreen(R(-2, -2, 1709, 962), Monitor, isMaximized: false));
    }

    [Fact]
    public void NormalWindow_IsNotFullscreen()
    {
        var window = R(321, 124, 1601, 789);
        Assert.False(FullscreenDetection.IsFullscreen(window, Monitor, isMaximized: false));
    }

    [Fact]
    public void EmptyRect_IsNotFullscreen()
    {
        // GetWindowRect 拿到空矩形时的退路：零面积不能因为“满足覆盖”而误判成全屏。
        Assert.False(FullscreenDetection.IsFullscreen(R(0, 0, 0, 0), Monitor, isMaximized: false));
    }

    // ==================== 四条边：任一条没盖住就不算全屏 ====================

    [Theory]
    [InlineData(1, 0, 1707, 960)]   // 左边没盖住
    [InlineData(0, 1, 1707, 960)]   // 上边没盖住
    [InlineData(0, 0, 1706, 960)]   // 右边没盖住
    [InlineData(0, 0, 1707, 959)]   // 下边没盖住
    public void NotCoveringEveryEdge_IsNotFullscreen(int left, int top, int right, int bottom)
    {
        var window = R(left, top, right, bottom);
        Assert.False(FullscreenDetection.IsFullscreen(window, Monitor, isMaximized: false));
    }

    [Fact]
    public void WindowCoveringItsOwnMonitor_InMultiMonitorSetup_IsFullscreen()
    {
        // 副屏：传给判定的是“窗口所在那个显示器”的矩形，不是主屏的。
        var rightMonitor = R(1707, 0, 3414, 960);
        var window = R(1707, 0, 3414, 960);

        Assert.True(FullscreenDetection.IsFullscreen(window, rightMonitor, isMaximized: false));
    }
}
