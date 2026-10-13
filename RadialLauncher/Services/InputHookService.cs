using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using RadialLauncher.Models;

namespace RadialLauncher.Services
{
    // TriggerKind 连同全部手势状态机逻辑已抽到 HookGestureMachine.cs。
    // 本类只负责三件事：安装/卸载钩子、把原始事件喂给状态机、施加状态机返回的副作用。

    /// <summary>
    /// 用 WH_KEYBOARD_LL / WH_MOUSE_LL 全局钩子实现“按住 Ctrl / Win / 鼠标中键唤出轮盘”。
    ///
    /// 分工：手势判定（吞不吞、该补发什么）全在 <see cref="HookGestureMachine"/> 里，
    /// 那是纯逻辑、不碰 Win32 与 WPF，因此可以单独跑单元测试；
    /// 本类只管安装钩子、解析原始事件、把状态机返回的副作用落实到真实世界。
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
        public bool ListenCtrl { get => _machine.ListenCtrl; set => _machine.ListenCtrl = value; }
        public bool ListenWin { get => _machine.ListenWin; set => _machine.ListenWin = value; }
        public bool ListenMiddle { get => _machine.ListenMiddle; set => _machine.ListenMiddle = value; }
        public bool SuppressWin { get => _machine.SuppressWin; set => _machine.SuppressWin = value; }
        public bool SuppressMiddle { get => _machine.SuppressMiddle; set => _machine.SuppressMiddle = value; }
        public bool CancelOnOtherKey { get => _machine.CancelOnOtherKey; set => _machine.CancelOnOtherKey = value; }
        public int HoldThresholdMs { get => _machine.HoldThresholdMs; set => _machine.HoldThresholdMs = value; }

        /// <summary>
        /// 轮盘显示时左键是否用来取消。
        /// “松开触发键执行”模式下左键=取消；开启“点击执行”后左键才是选中，
        /// 由轮盘窗口自己的轮询去触发，钩子这边不能把它取消掉。
        /// </summary>
        public bool CancelOnLeftClick { get => _machine.CancelOnLeftClick; set => _machine.CancelOnLeftClick = value; }

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

        /// <summary>
        /// 手势状态机：所有“按住了什么、要不要吞、该补发什么”的判断都在它内部。
        /// 本类只负责安装钩子、把原始事件喂进去、施加它返回的副作用。
        /// </summary>
        private readonly HookGestureMachine _machine = new();

        private bool _disposed;

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
        public bool IsWheelVisible => _machine.IsFired;

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

        /// <summary>把鼠标消息号写成人看的名字，让慢回调告警能直接定位到具体事件。</summary>
        private static string DescribeMouseMessage(int msg) => msg switch
        {
            NativeMethods.WM_MOUSEMOVE => "WM_MOUSEMOVE",
            NativeMethods.WM_MBUTTONDOWN => "WM_MBUTTONDOWN",
            NativeMethods.WM_MBUTTONUP => "WM_MBUTTONUP",
            NativeMethods.WM_LBUTTONDOWN => "WM_LBUTTONDOWN",
            NativeMethods.WM_RBUTTONDOWN => "WM_RBUTTONDOWN",
            _ => "msg=" + msg,
        };

        /// <summary>把键盘消息号写成人看的名字（只区分按下/松开，够定位了）。</summary>
        private static string DescribeKeyboardMessage(int msg) => msg switch
        {
            NativeMethods.WM_KEYDOWN => "WM_KEYDOWN",
            NativeMethods.WM_SYSKEYDOWN => "WM_SYSKEYDOWN",
            NativeMethods.WM_KEYUP => "WM_KEYUP",
            NativeMethods.WM_SYSKEYUP => "WM_SYSKEYUP",
            _ => "msg=" + msg,
        };

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
            ApplyEffects(_machine.ResetState());
        }

        /// <summary>暂停监听（设置面板打开期间使用）。</summary>
        public void Pause() => ApplyEffects(_machine.Pause());

        public void Resume() => _machine.Resume();

        // ============================ 键盘钩子 ============================

        private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0) return NativeMethods.CallNextHookEx(_kbHook, nCode, wParam, lParam);

            var result = HookResult.None;
            long start = Stopwatch.GetTimestamp();
            int msg = 0;   // 提前声明：finally 里的慢回调告警要带上它

            try
            {
                msg = wParam.ToInt32();
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

                if (_machine.Paused) return NativeMethods.CallNextHookEx(_kbHook, nCode, wParam, lParam);

                switch (vk)
                {
                    case NativeMethods.VK_CONTROL:
                    case NativeMethods.VK_LCONTROL:
                    case NativeMethods.VK_RCONTROL:
                        result = _machine.CtrlEvent(isDown);
                        break;

                    case NativeMethods.VK_LWIN:
                    case NativeMethods.VK_RWIN:
                        result = _machine.WinEvent(isDown);
                        break;

                    case NativeMethods.VK_ESCAPE:
                        if (isDown) result = _machine.EscapeDown();
                        break;

                    default:
                        if (isDown) result = _machine.OtherKeyDown();
                        break;
                }

                ApplyEffects(result.Effects);
            }
            catch (Exception ex)
            {
                // 钩子里的异常也延迟记录：写日志是文件 I/O，不能拖慢钩子返回
                Post(() => Logger.Error("键盘钩子异常", ex));
            }
            finally
            {
                ReportSlowHook(TicksToMs(Stopwatch.GetTimestamp() - start),
                    "键盘 " + DescribeKeyboardMessage(msg));
            }

            return result.Swallow
                ? (IntPtr)1
                : NativeMethods.CallNextHookEx(_kbHook, nCode, wParam, lParam);
        }

        // ============================ 鼠标钩子 ============================

        private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0) return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);

            var result = HookResult.None;
            long start = Stopwatch.GetTimestamp();
            int msg = 0;   // 提前声明：finally 里的慢回调告警要带上它

            try
            {
                msg = wParam.ToInt32();
                var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);

                // 同上：只过滤自己合成的鼠标事件
                if (data.dwExtraInfo == NativeMethods.Signature)
                    return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);

                if (_machine.Paused) return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);

                var pt = new HookPoint(data.pt.X, data.pt.Y);

                switch (msg)
                {
                    case NativeMethods.WM_MBUTTONDOWN:
                        // 中键不再“一按下就拦截”：先原样放行，按住超过设定时间才接管。
                        result = _machine.MiddleButtonDown(pt);
                        break;

                    case NativeMethods.WM_MBUTTONUP:
                        // 无论监听是否仍开着都交给状态机统一判断：
                        // 它内部已覆盖“不是本程序武装的那次、但接管时补发过松开”的兜底路径。
                        result = _machine.MiddleButtonUp();
                        break;

                    case NativeMethods.WM_MOUSEMOVE:
                        result = _machine.MouseMove(pt);
                        break;

                    case NativeMethods.WM_LBUTTONDOWN:
                        // 左键：取消模式下用来取消轮盘；点击执行模式下要放给
                        // WheelWindow 的轮询去完成“选中并执行”。
                        result = _machine.LeftButtonDown();
                        break;

                    case NativeMethods.WM_RBUTTONDOWN:
                        // 右键始终是取消
                        result = _machine.RightButtonDown();
                        break;
                }

                ApplyEffects(result.Effects);
            }
            catch (Exception ex)
            {
                Post(() => Logger.Error("鼠标钩子异常", ex));
            }
            finally
            {
                ReportSlowHook(TicksToMs(Stopwatch.GetTimestamp() - start),
                    "鼠标 " + DescribeMouseMessage(msg));
            }

            return result.Swallow
                ? (IntPtr)1
                : NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        // ============================ 施加副作用 ============================
        //
        // 状态机（HookGestureMachine）只回答两件事：这次事件吞不吞、宿主该做什么。
        // 真正的动作都在这里落地：计时器、合成输入、UI 事件。
        // 拆开的好处是状态机可以脱离钩子与 WPF 单独测试 —— 最容易出错的恰恰是它的边界行为。

        /// <summary>
        /// 把状态机返回的副作用落实到真实世界。
        ///
        /// <para>调用点有两处：钩子回调内（大多数事件），以及计时器到点后（<see cref="OnHoldElapsed"/>）。
        /// 前者必须保持轻量：合成输入一律用 <see cref="Post"/> 投递到消息循环，
        /// 绝不能在这个栈上同步执行。</para>
        /// </summary>
        private void ApplyEffects(HookEffect effects)
        {
            if ((effects & HookEffect.StopHoldTimer) != 0) _holdTimer.Stop();

            if ((effects & HookEffect.StartHoldTimer) != 0)
            {
                _holdTimer.Stop();
                _holdTimer.Interval = TimeSpan.FromMilliseconds(_machine.EffectiveHoldMs);
                _holdTimer.Start();
            }

            if ((effects & HookEffect.ReplayWinTap) != 0)
            {
                // 轻点：延迟补发完整的按下+松开，让系统原有行为照常发生
                Post(() =>
                {
                    KeySequence.KeyDown(NativeMethods.VK_LWIN);
                    KeySequence.KeyUp(NativeMethods.VK_LWIN);
                });
            }

            if ((effects & HookEffect.ReplayWinDown) != 0)
                Post(() => KeySequence.KeyDown(NativeMethods.VK_LWIN));

            if ((effects & HookEffect.ReplayWinUp) != 0)
                Post(() => KeySequence.KeyUp(NativeMethods.VK_LWIN));

            if ((effects & HookEffect.InjectMiddleUp) != 0) InjectMiddleUp();

            if ((effects & HookEffect.EvaluateTrigger) != 0) EvaluateTrigger();

            if ((effects & HookEffect.ShowWheel) != 0) ShowWheel();

            if ((effects & HookEffect.HideWheel) != 0) PostCancel();

            if ((effects & HookEffect.HideWheelAndExecute) != 0) PostRelease();
        }

        /// <summary>
        /// 环境判定。判定完成后结果一律交回状态机复核，由它决定这次手势还算不算数 ——
        /// 判定期间用户可能已经松手（后续把钩子搬到专用线程后这个窗口会更明显），
        /// 交给状态机复核才不会出现“轮盘闪一下就没了”。
        /// </summary>
        private void EvaluateTrigger()
        {
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

            ApplyEffects(_machine.ResolveTrigger(allowed));
        }

        /// <summary>唤出轮盘。运行在消息循环里，不在钩子回调内。</summary>
        private void ShowWheel()
        {
            try
            {
                OnTrigger?.Invoke();
            }
            catch (Exception ex)
            {
                Logger.Error("唤出轮盘失败", ex);
                _machine.RollbackFired();
            }
        }

        /// <summary>
        /// 接管中键：按下时已经原样放行给系统了，现在必须补一次“松开”，
        /// 否则程序会一直以为中键按着（浏览器会卡在中键滚动模式，鼠标一动页面就滚）。
        /// </summary>
        private void InjectMiddleUp()
        {
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

        /// <summary>取消：隐藏轮盘，不执行任何功能。可能被钩子调用，所以只能投递。</summary>
        private void PostCancel()
        {
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

        /// <summary>松开执行：隐藏轮盘并执行选中项。可能被钩子调用，所以只能投递。</summary>
        private void PostRelease()
        {
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

        /// <summary>
        /// 阈值到达：计时到点后由 DispatcherTimer 调用，运行在消息循环里（不在钩子回调内），
        /// 所以这里可以安全地做环境判定和唤出轮盘。
        /// </summary>
        private void OnHoldElapsed() => ApplyEffects(_machine.HoldElapsed());

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            lock (_gate)
            {
                // 顺序很重要：先摘钩子，确保后面补发的按键不会再走进我们自己的回调，
                // 也避免在进程退出过程中还留着内核钩子（那正是进程退不掉的原因）。
                // 残留按键要在清理之前问状态机 —— ResetState 会把标志位清掉。
                var abort = _machine.ComputeAbortEffects();

                Stop();

                try
                {
                    if ((abort & HookEffect.ReplayWinUp) != 0) KeySequence.KeyUp(NativeMethods.VK_LWIN);
                    if ((abort & HookEffect.InjectMiddleUp) != 0)
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
