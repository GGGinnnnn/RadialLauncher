using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Windows.Threading;
using RadialLauncher.Models;

namespace RadialLauncher.Services
{
    /// <summary>前台窗口信息快照。</summary>
    public sealed class ForegroundWindowInfo
    {
        public IntPtr Handle { get; init; }
        public string ProcessName { get; init; } = "";
        public string Title { get; init; } = "";
        public string ClassName { get; init; } = "";
        public bool IsFullscreen { get; init; }
        public bool IsShell { get; init; }

        public string Display => string.IsNullOrEmpty(Title)
            ? ProcessName
            : $"{ProcessName} - {Title}";
    }

    /// <summary>可加入白/黑名单的窗口条目。</summary>
    public sealed class WindowEntry
    {
        public IntPtr Handle { get; set; }
        public string ProcessName { get; set; } = "";
        public string Title { get; set; } = "";
        public string Display => string.IsNullOrWhiteSpace(Title)
            ? ProcessName
            : $"{ProcessName}  —  {Title}";
    }

    /// <summary>
    /// 检测前台窗口，并判断当前窗口是否允许唤出轮盘。
    /// 带短缓存，保证在钩子回调里调用也不会拖慢输入。
    /// </summary>
    public sealed class WindowDetectorService
    {
        private static readonly TimeSpan CacheTtl = TimeSpan.FromMilliseconds(120);

        private readonly DispatcherTimer _timer;
        private ForegroundWindowInfo _cached = new();
        private DateTime _cachedAt = DateTime.MinValue;

        /// <summary>本进程名，用于排除自己的窗口。</summary>
        private readonly string _selfProcess =
            Process.GetCurrentProcess().ProcessName;

        public WindowDetectorService()
        {
            _timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(500),
            };
            _timer.Tick += (_, _) => Refresh();
        }

        public ForegroundWindowInfo Foreground => GetCurrent();

        public void Start()
        {
            Refresh();
            _timer.Start();
        }

        public void Stop() => _timer.Stop();

        private void Refresh() => GetCurrent(force: true);

        public ForegroundWindowInfo GetCurrent(bool force = false)
        {
            var now = DateTime.UtcNow;
            if (!force && now - _cachedAt < CacheTtl) return _cached;

            _cached = Query();
            _cachedAt = now;
            return _cached;
        }

        private ForegroundWindowInfo Query()
        {
            try
            {
                var hwnd = NativeMethods.GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return new ForegroundWindowInfo();

                NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);

                string procName = "";
                if (pid != 0)
                {
                    try { procName = Process.GetProcessById((int)pid).ProcessName; }
                    catch { procName = ""; }
                }

                var title = new StringBuilder(512);
                NativeMethods.GetWindowTextW(hwnd, title, title.Capacity);

                var cls = new StringBuilder(256);
                NativeMethods.GetClassNameW(hwnd, cls, cls.Capacity);

                string className = cls.ToString();

                return new ForegroundWindowInfo
                {
                    Handle = hwnd,
                    ProcessName = procName,
                    Title = title.ToString(),
                    ClassName = className,
                    IsFullscreen = CheckFullscreen(hwnd),
                    IsShell = IsShellWindow(procName, className),
                };
            }
            catch (Exception ex)
            {
                Logger.Debug("查询前台窗口失败：" + ex.Message);
                return new ForegroundWindowInfo();
            }
        }

        private bool IsShellWindow(string processName, string className)
        {
            if (className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
                return true;
            return string.Equals(processName, "explorer", StringComparison.OrdinalIgnoreCase)
                   && className.StartsWith("Shell_", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 窗口矩形覆盖整个显示器、且不是“最大化”状态 → 认为处于全屏（游戏 / 全屏视频）。
        /// 这里只负责取数（矩形 / 显示器 / 最大化状态）与异常兜底，
        /// 判定规则本身在 <see cref="FullscreenDetection"/>，可单测。
        /// </summary>
        private static bool CheckFullscreen(IntPtr hwnd)
        {
            try
            {
                if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return false;

                var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
                if (monitor == IntPtr.Zero) return false;

                var mi = new NativeMethods.MONITORINFO
                {
                    cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>(),
                };
                if (!NativeMethods.GetMonitorInfoW(monitor, ref mi)) return false;

                // IsZoomed 这一项是修掉“最大化被当成全屏”的关键，别删。
                return FullscreenDetection.IsFullscreen(
                    new ScreenRect(rect.Left, rect.Top, rect.Right, rect.Bottom),
                    new ScreenRect(
                        mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right, mi.rcMonitor.Bottom),
                    NativeMethods.IsZoomed(hwnd));
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 黑白名单判定（不含应用专属逻辑）。
        ///
        /// <para>本方法只在“按住到达阈值、准备唤出”这一刻被调用（唯一调用方是
        /// <c>MainWindow.CanTrigger</c>），频率等于用户按压次数，所以每个
        /// <c>return false</c> 都带上一句原因日志是安全的，不会刷屏。</para>
        ///
        /// <para>这么做是因为全屏那条拦截过去完全静默 —— 日志里连一行都没有，
        /// 只能靠真机做对照实验才反推出“是最大化窗口被误判成全屏”。</para>
        /// </summary>
        public bool IsAllowed(AppConfig config, ForegroundWindowInfo? info = null)
        {
            info ??= GetCurrent();

            // 自己的窗口（设置面板等）里不唤出轮盘，避免打断输入框里的编辑
            if (string.Equals(info.ProcessName, _selfProcess, StringComparison.OrdinalIgnoreCase))
            {
                Logger.Debug("前台是本程序自己的窗口，拦截本次唤出");
                return false;
            }

            if (string.IsNullOrEmpty(info.ProcessName)) return true;

            var env = config.Environment;

            if (env.SkipFullscreenApps && info.IsFullscreen && !info.IsShell)
            {
                // 用 Info 而不是 Debug：默认日志级别就是 Info，而这条恰恰是过去
                // “按了没反应、日志还干干净净”的元凶，必须能看见。
                Logger.Info($"前台处于全屏，按 SkipFullscreenApps 拦截本次唤出：{info.Display}");
                return false;
            }

            if (env.UseWhitelist)
            {
                if (ContainsIgnoreCase(env.WhitelistProcesses, info.ProcessName)) return true;

                // 下面两条是用户自己配的策略，属于“预期内”，用 Debug 就好，免得刷屏。
                Logger.Debug($"前台不在白名单内，拦截本次唤出：{info.ProcessName}");
                return false;
            }

            if (ContainsIgnoreCase(env.BlacklistProcesses, info.ProcessName))
            {
                Logger.Debug($"前台在黑名单内，拦截本次唤出：{info.ProcessName}");
                return false;
            }

            return true;
        }

        private static bool ContainsIgnoreCase(List<string> list, string value)
        {
            foreach (var s in list)
            {
                if (string.Equals(s, value, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>枚举当前有可见顶层窗口的进程，供设置面板的“窗口检测器”使用。</summary>
        public static List<WindowEntry> EnumerateWindows()
        {
            var result = new List<WindowEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                NativeMethods.EnumWindows((hwnd, _) =>
                {
                    try
                    {
                        if (!NativeMethods.IsWindowVisible(hwnd)) return true;

                        var title = new StringBuilder(512);
                        NativeMethods.GetWindowTextW(hwnd, title, title.Capacity);
                        string t = title.ToString();
                        if (string.IsNullOrWhiteSpace(t)) return true;

                        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
                        if (pid == 0) return true;

                        string procName;
                        try { procName = Process.GetProcessById((int)pid).ProcessName; }
                        catch { return true; }

                        if (string.IsNullOrEmpty(procName)) return true;

                        // 同一个进程只保留第一条（最相关的那条标题）
                        if (!seen.Add(procName)) return true;

                        result.Add(new WindowEntry
                        {
                            Handle = hwnd,
                            ProcessName = procName,
                            Title = t,
                        });
                    }
                    catch
                    {
                        // 单个窗口读取失败不影响整体枚举
                    }

                    return true;
                }, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                Logger.Error("枚举窗口失败", ex);
            }

            result.Sort((a, b) => string.Compare(a.ProcessName, b.ProcessName, StringComparison.OrdinalIgnoreCase));
            return result;
        }
    }
}
