namespace RadialLauncher.Services
{
    /// <summary>
    /// 前台窗口属于哪一类 shell 表面。
    /// </summary>
    public enum ShellSurface
    {
        /// <summary>普通应用窗口，不是 shell 表面。</summary>
        None = 0,

        /// <summary>桌面（含壁纸层）。</summary>
        Desktop,

        /// <summary>任务栏（主屏或扩展屏）。</summary>
        Taskbar,

        /// <summary>explorer.exe 名下其余的 shell 窗口（通知区浮出层、托盘溢出面板等）。</summary>
        ShellChrome,
    }

    /// <summary>
    /// “这个前台窗口是不是 Windows 的 shell 表面”的纯判定：只吃进程名与窗口类名，不碰 Win32、
    /// 不碰 WPF，因此可以直接单元测试。<br/>
    /// 与 <see cref="FullscreenDetection"/> 同样的路子：把判定从 <c>WindowDetectorService.Query</c>
    /// 的 P/Invoke 调用中间摘出来。
    ///
    /// <para><b>为什么这件事必须单独判定：</b>桌面、壁纸层、任务栏<b>全部由 explorer.exe 承载</b>，
    /// 只比进程名的话，桌面和真正的资源管理器窗口完全无法区分。曾经就是因为这一点，
    /// 在桌面上唤出的轮盘变成了「资源管理器」的文件夹专用轮盘（Alt+D / Ctrl+E / Delete 全无意义）。</para>
    ///
    /// <para><b>关键区分点：</b>资源管理器窗口的窗口类是 <c>CabinetWClass</c>，不以 <c>Shell_</c>
    /// 开头、也不等于 <c>Progman</c> / <c>WorkerW</c>，所以这里返回
    /// <see cref="ShellSurface.None"/>，仍按普通应用对待。</para>
    /// </summary>
    public static class ShellWindowDetection
    {
        /// <summary>
        /// 判断窗口属于哪一类 shell 表面。
        /// </summary>
        /// <param name="processName">窗口所属进程名（<c>Process.ProcessName</c>，不含 .exe）。</param>
        /// <param name="className">窗口类名（<c>GetClassNameW</c>）。</param>
        /// <returns>普通应用窗口返回 <see cref="ShellSurface.None"/>。</returns>
        public static ShellSurface Classify(string? processName, string? className)
        {
            if (string.IsNullOrEmpty(className)) return ShellSurface.None;

            // 桌面。Progman 是常态；开了壁纸幻灯片或第三方动态壁纸后，桌面会被换到 WorkerW。
            // 这两个类名刻意不校验进程：动态壁纸确实是别的进程创建的 WorkerW，
            // 但它照样是桌面的一部分，不该被当成应用窗口。
            if (className is "Progman" or "WorkerW") return ShellSurface.Desktop;

            // 任务栏：主屏 Shell_TrayWnd，扩展屏 Shell_SecondaryTrayWnd。
            if (className is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return ShellSurface.Taskbar;

            // explorer.exe 名下其余的 Shell_* 窗口（通知区浮出层等）。资源管理器窗口类是
            // CabinetWClass，不会命中这里。
            if (string.Equals(processName, "explorer", StringComparison.OrdinalIgnoreCase)
                && className.StartsWith("Shell_", StringComparison.OrdinalIgnoreCase))
            {
                return ShellSurface.ShellChrome;
            }

            return ShellSurface.None;
        }

        /// <summary>是否属于 shell 表面（桌面 / 任务栏等）。</summary>
        public static bool IsShell(string? processName, string? className)
            => Classify(processName, className) != ShellSurface.None;
    }
}
