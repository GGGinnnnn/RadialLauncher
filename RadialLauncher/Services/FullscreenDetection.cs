namespace RadialLauncher.Services
{
    /// <summary>
    /// 屏幕坐标系下的矩形（左上 / 右下，单位像素）。
    /// 纯几何模块自带的小值类型 —— 刻意不直接用 <c>NativeMethods.RECT</c>：
    /// 那个结构在 internal 的 <c>NativeMethods</c> 里，判定函数不该把 P/Invoke 的
    /// 内部类型暴露成自己的公共签名，也不该反过来被它绑住。
    /// </summary>
    /// <param name="Left">左边界（屏幕坐标）。</param>
    /// <param name="Top">上边界（屏幕坐标）。</param>
    /// <param name="Right">右边界（屏幕坐标，不含）。</param>
    /// <param name="Bottom">下边界（屏幕坐标，不含）。</param>
    public readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left;

        public int Height => Bottom - Top;
    }

    /// <summary>
    /// “前台窗口算不算全屏”的纯几何判定。纯函数、不碰 Win32 与 WPF，因此可以直接单元测试 ——
    /// 这段逻辑以前整个埋在 <c>WindowDetectorService.CheckFullscreen</c> 的 Win32 调用中间，
    /// 只能靠真机反复试，边界情况（尤其是“最大化”这一条）一直没人验证过。<br/>
    /// 与 <see cref="WheelGeometry"/> 同样的路子：把判定从调用现场摘出来，让边界能进断言。
    ///
    /// <para>为什么要专门把“最大化”摘出去：工作区（<c>rcWork</c>）在任务栏自动隐藏时
    /// 与显示器区域（<c>rcMonitor</c>）完全重合，而 Windows 10/11 的最大化窗口会向外
    /// 溢出版面约 7px（实测窗口 <c>(-7,-7,1714,968)</c> vs 显示器 <c>(0,0,1707,960)</c>）。
    /// 只比“窗口矩形是否覆盖显示器”，一个再普通不过的最大化窗口就能满足
    /// <c>Left &lt;= monitor.Left</c> 和 <c>Right &gt;= monitor.Right</c>，于是被判成全屏；
    /// 默认开启的 <c>Environment.SkipFullscreenApps</c> 再把轮盘拦掉，
    /// 表现就是“按住 Ctrl 没反应，日志里也没有任何痕迹”。</para>
    /// </summary>
    public static class FullscreenDetection
    {
        /// <summary>
        /// 窗口矩形覆盖整个显示器、且不是最大化状态 → 认为处于全屏（游戏 / 全屏视频）。
        /// </summary>
        /// <param name="window">窗口矩形（<c>GetWindowRect</c>，屏幕坐标系）。</param>
        /// <param name="monitor">窗口所在显示器的<b>完整</b>矩形（<c>rcMonitor</c>，不是 <c>rcWork</c>）。</param>
        /// <param name="isMaximized">窗口是否处于最大化状态（<c>IsZoomed</c>）。</param>
        /// <returns>true 表示判定为全屏。</returns>
        public static bool IsFullscreen(ScreenRect window, ScreenRect monitor, bool isMaximized)
        {
            // 最大化 ≠ 全屏。真正的全屏（游戏 / F11 / 播放器）用的是无边框弹出窗口，
            // 一般不带 WS_MAXIMIZE，所以这一条不会误伤它们。
            if (isMaximized) return false;

            // 刻意用 <= / >= 而不是 ==：全屏窗口为了盖住自己的边框，通常会比显示器略大一圈，
            // 要求完全相等反而会把真全屏漏掉。
            return window.Left <= monitor.Left
                   && window.Top <= monitor.Top
                   && window.Right >= monitor.Right
                   && window.Bottom >= monitor.Bottom;
        }
    }
}
