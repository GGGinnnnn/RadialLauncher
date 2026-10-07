using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using RadialLauncher.Models;

namespace RadialLauncher.Services
{
    /// <summary>当前按住的是哪个触发键。</summary>
    public enum TriggerKind
    {
        None,
        Ctrl,
        Win,
        Middle,
    }

    /// <summary>
    /// 用 WH_KEYBOARD_LL / WH_MOUSE_LL 全局钩子实现“按住 Ctrl / Win / 鼠标中键唤出轮盘”。
    ///
    /// 关键设计：
    ///  - Ctrl 只观察不拦截（单独按 Ctrl 本来就没有系统行为，且 Ctrl+C 等组合键必须保持可用）。
    ///  - Win 与鼠标中键**先拦截不转发**，因为单独按它们分别会弹出开始菜单、触发中键行为。
    ///      · 若只是“轻点”（没到阈值就松开）→ 补发一次真实按键，保持系统原有行为。
    ///      · 若按住期间按了别的键（如 Win+E）→ 立刻补发 Win 按下，让组合键正常工作。
    ///  - 按住超过阈值且环境允许时才唤出轮盘；被黑白名单拦截时等同轻点。
    ///
    /// ★ 铁律：钩子回调里只允许改状态标志、启停 DispatcherTimer。
    ///   绝不能在回调里执行用户动作、合成输入、写日志或做任何 I/O。
    ///
    ///   原因：Windows 对低级钩子有 300ms 的超时（LowLevelHooksTimeout），
    ///   而且钩子回调是在自己的消息泵线程上同步调用的。以前在回调里直接
    ///   执行动作（Process.Start 打开浏览器、SendInput 发快捷键、写日志文件），
    ///   一旦这些调用耗时过长或与钩子链相互等待，就会出现：
    ///     · 界面线程卡死 → 托盘无响应、程序打不开
    ///     · SendInput 与钩子链互相等待 → 死锁，进程连 TerminateProcess 都退不掉
    ///   所以现在一律通过 BeginInvoke 把工作丢回消息循环，钩子立刻返回。
    /// </summary>
    public sealed class InputHookService : IDisposable
    {
        // ===== 可配置项（由主窗口从 AppConfig 同步过来）=====
        public bool ListenCtrl { get; set; } = true;
        public bool ListenWin { get; set; }
        public bool ListenMiddle { get; set; }
        public bool SuppressWin { get; set; } = true;
        public bool SuppressMiddle { get; set; } = true;
        public bool CancelOnOtherKey { get; set; } = true;
        public int HoldThresholdMs { get; set; } = 220;

        /// <summary>
        /// 轮盘显示时左键是否用来取消。
        /// “松开触发键执行”模式下左键=取消；开启“点击执行”后左键才是选中，
        /// 由轮盘窗口自己的轮询去触发，钩子这边不能把它取消掉。
        /// </summary>
        public bool CancelOnLeftClick { get; set; } = true;

        /// <summary>环境判定回调：返回 false 表示当前窗口不允许唤出轮盘。</summary>
        public Func<bool>? CanTrigger { get; set; }

        /// <summary>达到阈值、应当唤出轮盘。</summary>
        public event Action? OnTrigger;

        /// <summary>轮盘显示中，触发键被松开 → 隐藏并执行选中项。</summary>
        public event Action? OnRelease;

        /// <summary>轮盘显示中被取消（Esc / 其他按键 / 其他鼠标键）→ 只隐藏，不执行。</summary>
        public event Action? OnCancel;

        // ===== 内部状态 =====
        private readonly Dispatcher _dispatcher;
        private readonly DispatcherTimer _holdTimer;
        private readonly object _gate = new();

        private IntPtr _kbHook = IntPtr.Zero;
        private IntPtr _mouseHook = IntPtr.Zero;

        // 保持委托实例存活，否则会被 GC 回收导致钩子失效
        private readonly NativeMethods.LowLevelProc _kbProc;
        private readonly NativeMethods.LowLevelProc _mouseProc;

        private TriggerKind _armed = TriggerKind.None;
        private bool _fired;
        private bool _cancelled;

        private bool _winSwallowed;
        private bool _winReplayed;

        /// <summary>中键按下是否已经原样放行给系统（不做拦截）。</summary>
        private bool _midPassedThrough;

        /// <summary>接管中键时是否已经补发过一次“松开”。</summary>
        private bool _midReleaseInjected;

        private NativeMethods.POINT _midDownPoint;

        private bool _disposed;
        private bool _paused;

        /// <summary>中键拖动超过该像素距离即判定为“中键拖动”，不再拦截。</summary>
        private const int MiddleDragCancelPx = 40;

        /// <summary>
        /// 钩子回调超过该毫秒数就记一条警告。
        /// 正常应该在 1ms 以内；Windows 自己的上限是 300ms（LowLevelHooksTimeout），
        /// 一旦逼近这个值，钩子会被系统悄悄摘掉，界面也会卡住。
        /// </summary>
        private const double SlowHookWarnMs = 20.0;

        /// <summary>慢回调警告最多记这么多条，避免刷爆日志。</summary>
        private const int MaxSlowHookReports = 20;

        private int _slowHookReports;

        /// <summary>观测到的最长一次钩子回调耗时（诊断用）。</summary>
        public double MaxHookMs { get; private set; }

        public InputHookService()
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            _kbProc = KeyboardProc;
            _mouseProc = MouseProc;

            _holdTimer = new DispatcherTimer(DispatcherPriority.Input)
            {
                Interval = TimeSpan.FromMilliseconds(220),
            };
            _holdTimer.Tick += (_, _) => OnHoldElapsed();
        }

        public bool IsRunning => _kbHook != IntPtr.Zero || _mouseHook != IntPtr.Zero;

        /// <summary>轮盘是否正在显示。</summary>
        public bool IsWheelVisible => _fired;

        // ============================ 延迟执行 ============================

        /// <summary>
        /// 把工作排到消息循环里执行。
        /// 钩子回调里必须用它，绝不能用 Invoke/Send（那会在钩子里同步执行，等于没改）。
        /// </summary>
        private void Post(Action action)
        {
            if (_disposed) return;

            try
            {
                if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) return;
                _dispatcher.BeginInvoke(DispatcherPriority.Normal, action);
            }
            catch
            {
                // 消息循环已经没了就把这次投递丢掉，绝不能因为投递失败影响钩子返回
            }
        }

        /// <summary>钩子回调耗时超标时的告警（同样是延迟记录）。</summary>
        private void ReportSlowHook(double elapsedMs, string which)
        {
            if (elapsedMs > MaxHookMs) MaxHookMs = elapsedMs;

            if (elapsedMs < SlowHookWarnMs) return;
            if (_slowHookReports >= MaxSlowHookReports) return;

            _slowHookReports++;
            double elapsed = elapsedMs;
            Post(() => Logger.Warn($"钩子回调（{which}）耗时 {elapsed:0.0} ms，超过 {SlowHookWarnMs:0} ms 阈值"));
        }

        /// <summary>把 Stopwatch 时间戳换算成毫秒。</summary>
        private static double TicksToMs(long ticks)
            => ticks * 1000.0 / Stopwatch.Frequency;

        // ============================ 安装 / 卸载 ============================

        public void Start()
        {
            if (_disposed) return;

            if (_kbHook == IntPtr.Zero)
            {
                _kbHook = NativeMethods.SetWindowsHookExW(
                    NativeMethods.WH_KEYBOARD_LL, _kbProc,
                    NativeMethods.GetModuleHandleW(null), 0);
                if (_kbHook == IntPtr.Zero)
                    Logger.Error("安装键盘钩子失败，错误码 " + Marshal.GetLastWin32Error());
                else
                    Logger.Info("键盘钩子已安装");
            }

            if (_mouseHook == IntPtr.Zero)
            {
                _mouseHook = NativeMethods.SetWindowsHookExW(
                    NativeMethods.WH_MOUSE_LL, _mouseProc,
                    NativeMethods.GetModuleHandleW(null), 0);
                if (_mouseHook == IntPtr.Zero)
                    Logger.Error("安装鼠标钩子失败，错误码 " + Marshal.GetLastWin32Error());
                else
                    Logger.Info("鼠标钩子已安装");
            }
        }

        /// <summary>摘掉钩子并清空状态（不补发任何按键）。</summary>
        public void Stop()
        {
            if (_kbHook != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_kbHook);
                _kbHook = IntPtr.Zero;
            }
            if (_mouseHook != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_mouseHook);
                _mouseHook = IntPtr.Zero;
            }
            ResetGestureState();
        }

        /// <summary>暂停监听（设置面板打开期间使用）。</summary>
        public void Pause()
        {
            _paused = true;
            ResetGestureState();
        }

        public void Resume() => _paused = false;

        // ============================ 键盘钩子 ============================

        private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0) return NativeMethods.CallNextHookEx(_kbHook, nCode, wParam, lParam);

            bool swallow = false;
            long start = Stopwatch.GetTimestamp();

            try
            {
                int msg = wParam.ToInt32();
                bool isDown = msg == NativeMethods.WM_KEYDOWN || msg == NativeMethods.WM_SYSKEYDOWN;
                bool isUp = msg == NativeMethods.WM_KEYUP || msg == NativeMethods.WM_SYSKEYUP;

                if (!isDown && !isUp) return NativeMethods.CallNextHookEx(_kbHook, nCode, wParam, lParam);

                var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
                int vk = (int)data.vkCode;

                // 只跳过“自己合成的”按键：靠 dwExtraInfo 里的标记识别。
                // 不能用 LLKHF_INJECTED，否则 AutoHotkey、自动化脚本、辅助功能软件
                // 注入的按键会被一并忽略。
                if (data.dwExtraInfo == NativeMethods.Signature)
                    return NativeMethods.CallNextHookEx(_kbHook, nCode, wParam, lParam);

                if (_paused) return NativeMethods.CallNextHookEx(_kbHook, nCode, wParam, lParam);

                switch (vk)
                {
                    case NativeMethods.VK_CONTROL:
                    case NativeMethods.VK_LCONTROL:
                    case NativeMethods.VK_RCONTROL:
                        if (ListenCtrl)
                        {
                            if (isDown) HandleCtrlDown();
                            else HandleCtrlUp();
                        }
                        break;

                    case NativeMethods.VK_LWIN:
                    case NativeMethods.VK_RWIN:
                        if (ListenWin)
                            swallow = isDown ? HandleWinDown() : HandleWinUp();
                        else if (isUp && _winSwallowed)
                        {
                            // 监听被关掉但之前吞过一次按下，补上松开（延迟补发）
                            _winSwallowed = false;
                            _winReplayed = false;
                            Post(() => KeySequence.KeyUp(NativeMethods.VK_LWIN));
                            swallow = true;
                        }
                        break;

                    case NativeMethods.VK_ESCAPE:
                        if (isDown && _fired)
                        {
                            CancelGesture();
                            swallow = true;
                        }
                        break;

                    default:
                        if (isDown) HandleOtherKeyDown(vk);
                        break;
                }
            }
            catch (Exception ex)
            {
                // 钩子里的异常也延迟记录：写日志是文件 I/O，不能拖慢钩子返回
                Post(() => Logger.Error("键盘钩子异常", ex));
            }
            finally
            {
                ReportSlowHook(TicksToMs(Stopwatch.GetTimestamp() - start), "键盘");
            }

            return swallow
                ? (IntPtr)1
                : NativeMethods.CallNextHookEx(_kbHook, nCode, wParam, lParam);
        }

        // ============================ 鼠标钩子 ============================

        private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0) return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);

            bool swallow = false;
            long start = Stopwatch.GetTimestamp();

            try
            {
                int msg = wParam.ToInt32();
                var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);

                // 同上：只过滤自己合成的鼠标事件
                if (data.dwExtraInfo == NativeMethods.Signature)
                    return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);

                if (_paused) return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);

                switch (msg)
                {
                    case NativeMethods.WM_MBUTTONDOWN:
                        // 中键不再“一按下就拦截”：先原样放行，按住超过设定时间才接管。
                        if (ListenMiddle) swallow = HandleMiddleDown(data.pt);
                        break;

                    case NativeMethods.WM_MBUTTONUP:
                        if (ListenMiddle) swallow = HandleMiddleUp();
                        else if (_midReleaseInjected)
                        {
                            _midReleaseInjected = false;
                            _midPassedThrough = false;
                            swallow = true;
                        }
                        break;

                    case NativeMethods.WM_MOUSEMOVE:
                        HandleMouseMove(data.pt);
                        break;

                    case NativeMethods.WM_LBUTTONDOWN:
                        // 左键：取消模式下用来取消轮盘；点击执行模式下要放给
                        // WheelWindow 的轮询去完成“选中并执行”。
                        if (_fired)
                        {
                            if (CancelOnLeftClick) CancelGesture();
                        }
                        else if (_armed != TriggerKind.None)
                        {
                            _cancelled = true;
                            _holdTimer.Stop();
                        }
                        break;

                    case NativeMethods.WM_RBUTTONDOWN:
                        // 右键始终是取消
                        if (_fired) CancelGesture();
                        else if (_armed != TriggerKind.None)
                        {
                            _cancelled = true;
                            _holdTimer.Stop();
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                Post(() => Logger.Error("鼠标钩子异常", ex));
            }
            finally
            {
                ReportSlowHook(TicksToMs(Stopwatch.GetTimestamp() - start), "鼠标");
            }

            return swallow
                ? (IntPtr)1
                : NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        // ============================ Ctrl ============================

        private void HandleCtrlDown()
        {
            if (_armed != TriggerKind.None) return;
            Arm(TriggerKind.Ctrl);
        }

        private void HandleCtrlUp()
        {
            if (_armed != TriggerKind.Ctrl) return;
            FinishGesture();
        }

        // ============================ Win ============================

        private bool HandleWinDown()
        {
            if (_armed != TriggerKind.None)
            {
                // 已经因为别的键武装了，Win 按下不参与
                return false;
            }

            Arm(TriggerKind.Win);
            _winSwallowed = SuppressWin;
            _winReplayed = false;
            return _winSwallowed;
        }

        private bool HandleWinUp()
        {
            if (_armed != TriggerKind.Win)
            {
                if (!_winSwallowed) return false;

                // 拦截期间监听被关掉等异常路径：补一个 up
                _winSwallowed = false;
                if (!_winReplayed)
                {
                    _winReplayed = false;
                    Post(() => KeySequence.KeyUp(NativeMethods.VK_LWIN));
                }
                return true;
            }

            bool fired = _fired;
            FinishGesture();

            if (_winReplayed)
            {
                // Windows 已经收到过 Win 按下，松开必须放行
                _winSwallowed = false;
                _winReplayed = false;
                return false;
            }

            if (!_winSwallowed) return false;

            _winSwallowed = false;

            if (!fired)
            {
                // 轻点：延迟补发完整的按下+松开，让系统原有行为照常发生
                Post(() =>
                {
                    KeySequence.KeyDown(NativeMethods.VK_LWIN);
                    KeySequence.KeyUp(NativeMethods.VK_LWIN);
                });
            }

            return true;
        }

        // ============================ 鼠标中键 ============================
        //
        // 中键和 Win 键的策略不同：**按下时先不拦截，原样交给系统**。
        //   · 轻点（没到阈值就松开）→ 什么都不做，和没装本程序时完全一样。
        //     不需要补发合成点击，也就不会遇到“注入的按键被某些程序忽略”的问题。
        //   · 阈值内明显拖动 → 判定为中键滚动，放弃唤出；按下本来就没拦，滚动完全是原生的。
        //   · 按住超过阈值 → 这时才接管：弹出轮盘，并补一次“松开”，
        //     否则程序会一直以为中键按着（浏览器会卡在中键滚动模式）。

        private bool HandleMiddleDown(NativeMethods.POINT pt)
        {
            if (_armed != TriggerKind.None) return false;

            Arm(TriggerKind.Middle);
            _midDownPoint = pt;
            _midPassedThrough = true;
            _midReleaseInjected = false;

            return false;   // 不拦截
        }

        /// <returns>true 表示吞掉这次真实的“中键松开”。</returns>
        private bool HandleMiddleUp()
        {
            if (_armed != TriggerKind.Middle)
            {
                // 不是本程序武装的那次中键；如果之前接管时补发过松开，
                // 这次真实松开要吞掉，免得程序收到两次松开。
                if (!_midReleaseInjected) return false;

                _midReleaseInjected = false;
                _midPassedThrough = false;
                return true;
            }

            bool fired = _fired;
            FinishGesture();

            bool injected = _midReleaseInjected;
            _midReleaseInjected = false;
            _midPassedThrough = false;

            if (!fired) return false;   // 轻点：原样放行

            return injected;            // 接管过就吞掉这次真实松开
        }

        private void HandleMouseMove(NativeMethods.POINT pt)
        {
            if (_armed != TriggerKind.Middle || _fired || !_midPassedThrough) return;

            int dx = pt.X - _midDownPoint.X;
            int dy = pt.Y - _midDownPoint.Y;

            // 中键按下后明显拖动 → 判定为“中键滚动”，放弃唤出。
            // 按下本来就是放行的，这里什么都不用补，原生滚动完全不受影响。
            if (dx * dx + dy * dy >= MiddleDragCancelPx * MiddleDragCancelPx)
            {
                _cancelled = true;
                _holdTimer.Stop();
            }
        }

        // ============================ 通用状态机 ============================

        private void HandleOtherKeyDown(int vk)
        {
            var kind = _armed;
            if (kind == TriggerKind.None) return;

            if (_fired)
            {
                // 轮盘显示中又按了别的键 → 取消轮盘，按键继续传递
                CancelGesture();
            }
            else if (!_cancelled && CancelOnOtherKey)
            {
                // 阈值内按了别的键 → 说明用户在做组合键（Ctrl+C / Win+E）
                _cancelled = true;
                _holdTimer.Stop();
            }

            if (kind == TriggerKind.Win && _winSwallowed && !_winReplayed)
            {
                _winReplayed = true;
                Post(() => KeySequence.KeyDown(NativeMethods.VK_LWIN));
            }
        }

        private void Arm(TriggerKind kind)
        {
            _armed = kind;
            _fired = false;
            _cancelled = false;

            _holdTimer.Stop();
            _holdTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(40, HoldThresholdMs));
            _holdTimer.Start();
        }

        /// <summary>
        /// 阈值到达。由 DispatcherTimer 触发，运行在消息循环里（不在钩子回调内），
        /// 所以这里可以安全地做环境判定和唤出轮盘。
        /// </summary>
        private void OnHoldElapsed()
        {
            _holdTimer.Stop();

            if (_armed == TriggerKind.None || _fired || _cancelled) return;

            bool allowed;
            try
            {
                allowed = CanTrigger?.Invoke() ?? true;
            }
            catch (Exception ex)
            {
                Logger.Error("环境判定异常，按不允许处理", ex);
                allowed = false;
            }

            if (!allowed)
            {
                // 被黑白名单或全屏检测拦截：按“轻点”处理，保留系统原有按键行为
                _cancelled = true;
                return;
            }

            // 中键：按下时已经原样放行给系统了，现在决定接管，
            // 必须补一次“松开”，否则程序会一直以为中键按着
            // （浏览器会卡在中键滚动模式，鼠标一动页面就滚）。
            // 这里运行在消息循环里、不在钩子回调内，可以安全地合成输入。
            if (_armed == TriggerKind.Middle && _midPassedThrough && SuppressMiddle && !_midReleaseInjected)
            {
                _midReleaseInjected = true;

                try
                {
                    NativeMethods.mouse_event(
                        NativeMethods.MOUSEEVENTF_MIDDLEUP, 0, 0, 0, NativeMethods.SignatureUInt);
                    Logger.Debug("接管中键：已补发一次中键松开");
                }
                catch (Exception ex)
                {
                    Logger.Warn("接管中键时补发松开失败：" + ex.Message);
                }
            }

            _fired = true;

            try
            {
                OnTrigger?.Invoke();
            }
            catch (Exception ex)
            {
                Logger.Error("唤出轮盘失败", ex);
                _fired = false;
            }
        }

        /// <summary>触发键松开：隐藏轮盘并执行选中项。可能被钩子调用，所以只能改状态 + 投递。</summary>
        private void FinishGesture()
        {
            _holdTimer.Stop();

            bool wasFired = _fired;
            _armed = TriggerKind.None;
            _fired = false;
            _cancelled = false;

            if (!wasFired) return;

            // ★ 执行动作会 Process.Start / SendInput / 写日志，必须离开钩子回调
            Post(() =>
            {
                try
                {
                    OnRelease?.Invoke();
                }
                catch (Exception ex)
                {
                    Logger.Error("轮盘松开处理失败", ex);
                }
            });
        }

        /// <summary>取消：隐藏轮盘，不执行任何功能。</summary>
        private void CancelGesture()
        {
            _holdTimer.Stop();

            bool wasFired = _fired;
            _armed = TriggerKind.None;
            _fired = false;
            _cancelled = false;

            if (!wasFired) return;

            Post(() =>
            {
                try
                {
                    OnCancel?.Invoke();
                }
                catch (Exception ex)
                {
                    Logger.Error("轮盘取消处理失败", ex);
                }
            });
        }

        /// <summary>只清状态，不补发任何按键。</summary>
        private void ResetGestureState()
        {
            _holdTimer.Stop();
            _armed = TriggerKind.None;
            _fired = false;
            _cancelled = false;
            _winSwallowed = false;
            _winReplayed = false;
            _midPassedThrough = false;
            _midReleaseInjected = false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            lock (_gate)
            {
                // 顺序很重要：先摘钩子，确保后面补发的按键不会再走进我们自己的回调，
                // 也避免在进程退出过程中还留着内核钩子（那正是进程退不掉的原因）。
                bool winStuck = _winSwallowed && !_winReplayed;
                bool midStuck = _midPassedThrough && !_midReleaseInjected;

                Stop();

                try
                {
                    if (winStuck) KeySequence.KeyUp(NativeMethods.VK_LWIN);
                    if (midStuck)
                    {
                        NativeMethods.mouse_event(
                            NativeMethods.MOUSEEVENTF_MIDDLEUP, 0, 0, 0, NativeMethods.SignatureUInt);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn("释放残留按键失败：" + ex.Message);
                }
            }
        }
    }
}
