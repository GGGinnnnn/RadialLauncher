using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
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
    /// ★ 线程模型（本类最重要的一条约定）
    ///
    ///   Windows 的低级钩子回调在**安装钩子的那个线程**上执行。以前钩子装在 UI 线程上，
    ///   于是每一次全局按键、每一次鼠标移动都在 WPF UI 线程上跑：界面一忙（渲染、布局、
    ///   设置面板、字体操作、轮盘动画），用户在所有程序里打字都会跟着卡；逼近 Windows
    ///   自己的 300ms 上限（LowLevelHooksTimeout）还会被系统静默绕过，表现为 Win 键
    ///   偶尔吞不掉、漏出开始菜单，或者错过一次 KeyUp 让手势状态卡死。
    ///
    ///   现在钩子装在一个专用线程（下称“钩子线程”）上，它自己跑 GetMessage 消息泵：
    ///
    ///     UI 线程（WPF Dispatcher）               钩子线程（MTA + 自有消息泵）
    ///     ──────────────────────────              ─────────────────────────────
    ///     SyncHookSettings → 不可变快照 ───────→  钩子回调读快照（无锁、无撕裂读）
    ///     Pause / Resume ──────────────────────→  WM_APP 消息：清手势状态
    ///     CanTrigger/OnTrigger/OnRelease/          WM_TIMER（SetTimer 长按计时）
    ///       OnCancel ←──── Dispatcher 回投 ────   状态机全部字段只在本线程读写
    ///
    ///   **核心不变量：状态机的每个字段只在钩子线程读写。** 做到这一点，就和锁、volatile、
    ///   Interlocked 全都无关；跨线程只传“不可变快照”和“请求消息”。
    ///
    /// ★ 铁律：钩子回调里只允许改状态标志、启停计时器。
    ///   绝不能在回调里执行用户动作、合成输入、写日志或做任何 I/O。
    ///
    ///   原因：低级钩子回调同步阻塞的是**整个系统的输入链**，而且合成输入会同步穿过本钩子
    ///   造成重入（历史上出现过 SendInput 与钩子链互相等待、进程连 TerminateProcess 都退不掉）。
    ///   所以补发按键一律排进钩子线程自己的消息队列，等回调返回后再执行。
    /// </summary>
    public sealed class InputHookService : IDisposable
    {
        /// <summary>
        /// 可由主窗口整体替换的设置快照。
        ///
        /// <para>钩子回调每秒可能被调用上百次，绝不能每次回调都去读 AppConfig（那是一份会被
        /// UI 线程随手改写的可变对象）。这里做成不可变记录 + 一次引用交换：UI 侧构造完整快照，
        /// 钩子线程只做一次引用读取，既不需要锁，也不会读到半新半旧的组合。</para>
        /// </summary>
        public sealed record HookSettings(
            bool ListenCtrl,
            bool ListenWin,
            bool ListenAlt,
            bool ListenMiddle,
            int[] CustomTriggerVks,
            bool SuppressWin,
            bool SuppressMiddle,
            bool CancelOnOtherKey,
            bool CancelOnLeftClick,
            int HoldThresholdMs);

        // ============================ 钩子线程的消息号 ============================

        private static class HookMessage
        {
            /// <summary>安装钩子（带应答）。</summary>
            public const uint Install = NativeMethods.WM_APP + 1;

            /// <summary>摘掉钩子并清状态（带应答）。</summary>
            public const uint Uninstall = NativeMethods.WM_APP + 2;

            /// <summary>设置快照或暂停标志变了，去同步一次。</summary>
            public const uint SyncSettings = NativeMethods.WM_APP + 3;

            /// <summary>环境判定的结果回来了（wParam != 0 表示允许唤出）。</summary>
            public const uint TriggerResolved = NativeMethods.WM_APP + 4;

            /// <summary>唤出轮盘失败，回滚 <c>_fired</c>。</summary>
            public const uint RollbackFired = NativeMethods.WM_APP + 5;

            /// <summary>有排队的补发按键要执行。</summary>
            public const uint RunPending = NativeMethods.WM_APP + 6;

            /// <summary>整体收尾：补残留按键 + 摘钩子 + 结束泵。</summary>
            public const uint Shutdown = NativeMethods.WM_APP + 7;
        }

        /// <summary>
        /// SetTimer 请求用的 id。注意 hWnd 传 null 时系统可能另发一个 id 回来，
        /// 所以真正用于比对的是 <c>_holdTimerId</c>（SetTimer 的返回值）。
        /// </summary>
        private const uint HoldTimerRequestId = 1;

        /// <summary>
        /// 环境判定回调：返回 false 表示当前窗口不允许唤出轮盘。
        /// 运行在 UI 线程（判定要读前台窗口/进程，那里有现成的检测服务与缓存）。
        /// </summary>
        public Func<bool>? CanTrigger { get; set; }

        /// <summary>达到阈值、应当唤出轮盘。运行在 UI 线程。</summary>
        public event Action? OnTrigger;

        /// <summary>轮盘显示中，触发键被松开 → 隐藏并执行选中项。运行在 UI 线程。</summary>
        public event Action? OnRelease;

        /// <summary>轮盘显示中被取消（Esc / 其他按键 / 其他鼠标键）→ 只隐藏，不执行。运行在 UI 线程。</summary>
        public event Action? OnCancel;

        // ============================ 跨线程交换的字段 ============================

        private readonly Dispatcher _dispatcher;

        /// <summary>
        /// 当前设置快照。UI 线程整体替换、钩子线程只读引用 ——
        /// volatile 保证交换对回调立即可见（这也让 Pause 的“立刻闭嘴”无需等消息）。
        /// </summary>
        private volatile HookSettings _settings = new(
            ListenCtrl: true,
            ListenWin: false,
            ListenAlt: false,
            ListenMiddle: false,
            CustomTriggerVks: [],
            SuppressWin: true,
            SuppressMiddle: true,
            CancelOnOtherKey: true,
            CancelOnLeftClick: true,
            HoldThresholdMs: 220);

        /// <summary>暂停请求。UI 线程置位后，钩子回调下一次就会立刻放行。</summary>
        private volatile bool _paused;

        /// <summary>钩子是否已装上（UI 侧读，用于 ApplyConfig 判断要不要 Start）。</summary>
        private volatile bool _running;

        private volatile bool _disposed;

        /// <summary>native 线程 id（0 = 钩子线程还没就绪）。PostThreadMessage 需要它。</summary>
        private volatile int _hookThreadId;

        private ManualResetEventSlim? _pendingAck;

        // ============================ 钩子线程独占的状态 ============================
        //
        // 下面这些字段只允许在钩子线程读写，因此不需要任何锁。
        // 唯一例外是 _machine 的构造（在 UI 线程 new 出来、线程启动后才被使用，
        // Thread.Start 自带内存屏障，不存在可见性问题）。

        /// <summary>
        /// 手势状态机：所有“按住了什么、要不要吞、该补发什么”的判断都在它内部。
        /// 本类只负责安装钩子、把原始事件喂进去、施加它返回的副作用。
        /// </summary>
        private readonly HookGestureMachine _machine = new()
        {
            // ★ 让状态机能在阈值到点那一刻反问“除了触发键还有别的键按着吗”。
            //   这是唯一覆盖“别的键先于触发键按下”的手段 —— 那种情况下武装时早就错过了
            //   它的 KeyDown，只看事件顺序永远发现不了（先按住 Shift 再按 Ctrl 即典型）。
            //
            //   代价可控：全键程扫描实测约 0.8ms，且只在“按住到阈值”这一刻发生一次，
            //   不在每次按键/鼠标移动的热路径上。
            IsVirtualKeyDown = KeyStateHelper.IsDown,
        };

        private Thread? _hookThread;
        private readonly ManualResetEventSlim _hookThreadReady = new(false);

        private IntPtr _kbHook = IntPtr.Zero;
        private IntPtr _mouseHook = IntPtr.Zero;

        // 保持委托实例存活，否则会被 GC 回收导致钩子失效
        private readonly NativeMethods.LowLevelProc _kbProc;
        private readonly NativeMethods.LowLevelProc _mouseProc;

        /// <summary>长按计时器的实际 id（0 = 没在计时）。</summary>
        private long _holdTimerId;

        /// <summary>已同步到状态机的那份快照，用来判断“设置变了吗”（一次引用比较）。</summary>
        private HookSettings? _appliedSettings;

        /// <summary>钩子线程本地的暂停镜像，避免每次回调都调 Pause()/Resume()。</summary>
        private bool _machinePaused;

        /// <summary>
        /// 待补发的按键动作。只由钩子线程读写，所以普通 Queue 就够 ——
        /// 排在这里而不是直接执行，是为了绝不在钩子回调的栈上合成输入（会重入）。
        /// </summary>
        private readonly Queue<Action> _pendingActions = new();

        /// <summary>
        /// 钩子回调超过该毫秒数就记一条警告。
        /// 正常应该在 1ms 以内；Windows 自己的上限是 300ms（LowLevelHooksTimeout），
        /// 一旦逼近这个值，钩子会被系统悄悄摘掉，界面也会卡住。
        /// </summary>
        private const double SlowHookWarnMs = 20.0;

        /// <summary>慢回调警告最多记这么多条，避免刷爆日志。</summary>
        private const int MaxSlowHookReports = 20;

        private int _slowHookReports;

        /// <summary>
        /// 观测到的最长一次钩子回调耗时（诊断用）。
        /// 只在钩子线程写、UI 线程在退出时读一次；只是个诊断值，不值得为它上锁。
        /// </summary>
        private double _maxHookMs;

        /// <inheritdoc cref="_maxHookMs"/>
        public double MaxHookMs => _maxHookMs;

        public InputHookService()
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            _kbProc = KeyboardProc;
            _mouseProc = MouseProc;
        }

        /// <summary>钩子是否已装上。UI 线程读，由钩子线程在装/卸完成后更新。</summary>
        public bool IsRunning => _running;

        /// <summary>轮盘是否正在显示（诊断用；读的是钩子线程的状态，只是一个 bool）。</summary>
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

        /// <summary>
        /// 把动作排进**钩子线程自己**的队列，等当前回调返回后执行。
        ///
        /// <para>以前这类补发是排到 UI 线程的，于是“UI 一忙，Win 轻点补发就延迟”（开始菜单
        /// 晚弹）。现在排给自己的泵，补发延迟与 UI 负载彻底解耦 —— 这正是搬到钩子线程想治的
        /// 那个“UI 忙”。</para>
        /// </summary>
        private void QueueAction(Action action)
        {
            bool wasIdle = _pendingActions.Count == 0;
            _pendingActions.Enqueue(action);

            // 只在队列由空转非空时通知一次，避免一次手势里连发好几条消息
            if (wasIdle) PostToHookThread(HookMessage.RunPending);
        }

        private void RunPendingActions()
        {
            while (_pendingActions.Count > 0)
            {
                var action = _pendingActions.Dequeue();
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Logger.Error("执行补发按键失败", ex);
                }
            }
        }

        /// <summary>
        /// 记录钩子回调耗时。
        ///
        /// <para><b>必须零分配。</b>低级钩子回调每秒可能被调用上千次（高回报率鼠标的
        /// WM_MOUSEMOVE 尤其密集），如果每次回调都去拼那句告警字符串，就会源源不断地
        /// 制造垃圾、把 Gen0 回收逼成高频事件。一旦某次 GC 暂停叠上开机时的磁盘/CPU 争抢
        /// 超过 Windows 的 LowLevelHooksTimeout（默认 300ms），系统会直接绕过本钩子 ——
        /// 表现就是整个系统的键鼠输入一起卡住、任务栏（explorer）失去响应甚至被重启。</para>
        ///
        /// <para>所以这里只做数值比较：没超阈值就直接返回，超了才拼字符串。</para>
        /// </summary>
        private void TrackHookDuration(double elapsedMs, bool isMouse, int msg)
        {
            if (elapsedMs > _maxHookMs) _maxHookMs = elapsedMs;

            if (elapsedMs < SlowHookWarnMs) return;
            if (_slowHookReports >= MaxSlowHookReports) return;

            _slowHookReports++;

            var which = isMouse
                ? "鼠标 " + DescribeMouseMessage(msg)
                : "键盘 " + DescribeKeyboardMessage(msg);

            Logger.Warn($"钩子回调（{which}）耗时 {elapsedMs:0.0} ms，超过 {SlowHookWarnMs:0} ms 阈值");
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

        // ============================ 钩子线程 ============================

        /// <summary>
        /// 首次需要时装上钩子线程并等它就绪。
        ///
        /// <para>线程 MTA、<c>IsBackground = true</c>：正常退出走 <see cref="Dispose"/> 的
        /// 有序收尾（发 WM_APP+Shutdown 再 Join）；万一某条路径漏掉了 Dispose，后台线程也不会
        /// 把进程吊在那儿退不掉 —— “进程退不掉”恰恰是这个钩子历史上最常见的事故形态。</para>
        /// </summary>
        private void EnsureHookThread()
        {
            if (_hookThread != null || _disposed) return;

            _hookThreadReady.Reset();

            var thread = new Thread(HookThreadMain)
            {
                Name = "RadialLauncher.HookThread",

                // 钩子线程 99% 的时间阻塞在 GetMessage 上，提一档优先级几乎不花成本，
                // 却能在 CPU 争抢时保证输入不被拖延。
                Priority = ThreadPriority.AboveNormal,

                IsBackground = true,
            };

            _hookThread = thread;
            thread.Start();

            if (!_hookThreadReady.Wait(2000))
                Logger.Warn("钩子线程 2 秒内未就绪，钩子可能装不上");
        }

        /// <summary>钩子线程主体：建消息队列 → 进 GetMessage 循环 → 分发自己的消息。</summary>
        private void HookThreadMain()
        {
            // 先 PeekMessage 一次，强制把本线程的消息队列建出来 ——
            // 向还没有消息队列的线程 PostThreadMessage 会直接失败（官方文档明示的竞态）。
            NativeMethods.PeekMessageW(out _, IntPtr.Zero, 0, 0, NativeMethods.PM_NOREMOVE);

            int tid = NativeMethods.GetCurrentThreadId();
            _hookThreadId = tid;
            _hookThreadReady.Set();
            Logger.Info($"钩子线程已就绪（tid={tid}，优先级={Thread.CurrentThread.Priority}）");

            try
            {
                while (true)
                {
                    int got = NativeMethods.GetMessageW(out var msg, IntPtr.Zero, 0, 0);
                    if (got == 0) break;        // WM_QUIT
                    if (got < 0)
                    {
                        Logger.Error("钩子线程取消息失败，错误码 " + Marshal.GetLastWin32Error());
                        break;
                    }

                    bool keepGoing;
                    try
                    {
                        keepGoing = HandleHookThreadMessage(msg);
                    }
                    catch (Exception ex)
                    {
                        // 一条消息处理失败不该带走整个泵
                        Logger.Error("钩子线程处理消息异常", ex);
                        keepGoing = true;
                    }

                    if (!keepGoing) break;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("钩子线程异常退出", ex);
            }
            finally
            {
                // 兜底：无论怎么退出，内核钩子都不应该留在系统里
                UnhookAll();
                KillHoldTimer();
                _hookThreadId = 0;
            }

            Logger.Info("钩子线程已退出");
        }

        /// <returns>false 表示应当结束消息泵。</returns>
        private bool HandleHookThreadMessage(NativeMethods.MSG msg)
        {
            switch (msg.message)
            {
                case NativeMethods.WM_TIMER:
                    if (_holdTimerId != 0 && (long)msg.wParam == _holdTimerId)
                    {
                        // 一次性：先停表，之后要不要继续由状态机的副作用决定
                        KillHoldTimer();
                        OnHoldElapsed();
                    }
                    break;

                case NativeMethods.WM_QUIT:
                    return false;

                case HookMessage.Install:
                    SyncSettings();
                    InstallHooks();
                    SignalAck();
                    break;

                case HookMessage.Uninstall:
                    UnhookAll();
                    ApplyEffects(_machine.ResetState());
                    SignalAck();
                    break;

                case HookMessage.SyncSettings:
                    SyncSettings();
                    break;

                case HookMessage.TriggerResolved:
                    // 判定的结果回钩子线程才裁决：判定跑在 UI 线程上，这期间用户可能已经
                    // 松手/取消。状态机会用“当前”状态重新校验，作废过时的判定。
                    ApplyEffects(_machine.ResolveTrigger(msg.wParam != IntPtr.Zero));
                    break;

                case HookMessage.RollbackFired:
                    _machine.RollbackFired();
                    break;

                case HookMessage.RunPending:
                    RunPendingActions();
                    break;

                case HookMessage.Shutdown:
                    ShutdownOnHookThread();
                    return false;
            }

            return true;
        }

        /// <summary>
        /// 进程退出前的收尾，跑在钩子线程上。
        ///
        /// <para>顺序很重要：先问状态机要残留按键（<see cref="HookGestureMachine.ResetState"/>
        /// 会把标志位清掉），再摘钩子 —— 确保后面补发的按键不会再走进我们自己的回调，
        /// 也避免在进程退出过程中还留着内核钩子（那正是进程退不掉的原因）。</para>
        /// </summary>
        private void ShutdownOnHookThread()
        {
            var abort = _machine.ComputeAbortEffects();

            UnhookAll();
            KillHoldTimer();

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

        /// <summary>安装两个钩子。只允许在钩子线程调用 —— 钩子绑定的是安装它的那个线程。</summary>
        private void InstallHooks()
        {
            if (_kbHook == IntPtr.Zero)
            {
                // hMod 传 IntPtr.Zero：低级钩子的回调在本进程内执行，不需要模块句柄。
                // 传 GetModuleHandleW(null) 在**单文件发布**下拿到的是 apphost 的模块句柄，
                // 与真正存放回调的程序集对不上 —— 表现为 SetWindowsHookEx 成功返回、
                // 日志也打印"钩子已安装"，但回调一次都不会被调用（整个触发功能失效）。
                _kbHook = NativeMethods.SetWindowsHookExW(
                    NativeMethods.WH_KEYBOARD_LL, _kbProc, IntPtr.Zero, 0);
                if (_kbHook == IntPtr.Zero)
                    Logger.Error("安装键盘钩子失败，错误码 " + Marshal.GetLastWin32Error());
                else
                    Logger.Info("键盘钩子已安装");
            }

            if (_mouseHook == IntPtr.Zero)
            {
                _mouseHook = NativeMethods.SetWindowsHookExW(
                    NativeMethods.WH_MOUSE_LL, _mouseProc, IntPtr.Zero, 0);
                if (_mouseHook == IntPtr.Zero)
                    Logger.Error("安装鼠标钩子失败，错误码 " + Marshal.GetLastWin32Error());
                else
                    Logger.Info("鼠标钩子已安装");
            }

            _running = _kbHook != IntPtr.Zero || _mouseHook != IntPtr.Zero;
        }

        /// <summary>摘掉两个钩子。只允许在钩子线程调用。</summary>
        private void UnhookAll()
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

            _running = false;
        }

        /// <summary>往钩子线程投一条消息（不等待）。</summary>
        private bool PostToHookThread(uint message, IntPtr payload = default)
        {
            int tid = _hookThreadId;
            if (tid == 0) return false;

            try
            {
                return NativeMethods.PostThreadMessageW((uint)tid, message, payload, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                Logger.Error("投递消息给钩子线程失败", ex);
                return false;
            }
        }

        /// <summary>投一条消息并等钩子线程处理完（带应答，用于安装/卸载这类需要立即生效的操作）。</summary>
        private bool PostToHookThreadAndWait(uint message, int timeoutMs = 2000)
        {
            var ack = new ManualResetEventSlim(false);
            Interlocked.Exchange(ref _pendingAck, ack);

            if (!PostToHookThread(message))
            {
                Interlocked.Exchange(ref _pendingAck, null);
                return false;
            }

            bool completed = ack.Wait(timeoutMs);

            // 先摘掉应答对象再返回：超时后才姗姗来迟的 SignalAck 只会拿到 null，
            // 不会去碰这个已经被调用方抛弃的对象。
            Interlocked.Exchange(ref _pendingAck, null);

            if (!completed)
                Logger.Warn($"等待钩子线程处理消息 {message} 超时（{timeoutMs} ms）");

            return completed;
        }

        /// <summary>钩子线程宣告“这条请求处理完了”。</summary>
        private void SignalAck()
        {
            var ack = Interlocked.Exchange(ref _pendingAck, null);
            try
            {
                ack?.Set();
            }
            catch (ObjectDisposedException)
            {
                // 调用方已超时走人，忽略
            }
        }

        // ============================ 长按计时 ============================
        //
        // 计时器同样只在钩子线程上起停：WM_TIMER 走的也是本线程的消息泵。
        // 这是用 SetTimer 而不是 System.Threading.Timer 的决定性理由 —— 后者的回调
        // 跑在线程池上，状态机的所有权会碎掉，就得给每个字段配锁。

        private void KillHoldTimer()
        {
            if (_holdTimerId == 0) return;

            NativeMethods.KillTimer(IntPtr.Zero, new UIntPtr((ulong)_holdTimerId));
            _holdTimerId = 0;
        }

        /// <summary>
        /// 起一次长按计时。hWnd 传 null 是官方支持的“线程计时器”用法：
        /// WM_TIMER 直接进本线程的消息队列，由本线程的泵分发。
        /// </summary>
        private void StartHoldTimer()
        {
            KillHoldTimer();

            long id = (long)NativeMethods.SetTimer(
                IntPtr.Zero,
                new UIntPtr(HoldTimerRequestId),
                (uint)_machine.EffectiveHoldMs,
                IntPtr.Zero);

            if (id == 0)
            {
                Logger.Warn("启动长按计时失败，错误码 " + Marshal.GetLastWin32Error());
                return;
            }

            _holdTimerId = id;
        }

        // ============================ 安装 / 卸载（对外） ============================

        /// <summary>
        /// 整体替换设置快照（UI 线程调用）。
        /// 钩子线程在下一次回调或收到同步消息后生效 —— 开关类配置本来就允许一拍延迟。
        /// </summary>
        public void UpdateSettings(HookSettings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);

            _settings = settings;
            PostToHookThread(HookMessage.SyncSettings);
        }

        /// <summary>安装钩子（UI 线程调用）。实际安装在钩子线程上完成，带应答。</summary>
        public void Start()
        {
            if (_disposed) return;

            EnsureHookThread();
            PostToHookThreadAndWait(HookMessage.Install);
        }

        /// <summary>摘掉钩子并清空状态（不补发任何按键）。</summary>
        public void Stop()
        {
            if (_disposed || _hookThread == null) return;

            PostToHookThreadAndWait(HookMessage.Uninstall);
        }

        /// <summary>
        /// 暂停监听（设置面板打开期间使用）。
        ///
        /// <para>置位走 <see cref="_paused"/>（volatile），钩子回调下一次就立刻放行，不等任何消息；
        /// 清手势状态则投回钩子线程执行，因为那些字段归它独占。</para>
        /// </summary>
        public void Pause()
        {
            _paused = true;
            PostToHookThread(HookMessage.SyncSettings);
        }

        public void Resume()
        {
            _paused = false;
            PostToHookThread(HookMessage.SyncSettings);
        }

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

                // 设置快照/暂停标志可能已经变了，先同步过来（一次引用比较，极廉价）
                SyncSettings();

                if (_paused || _machine.Paused)
                    return NativeMethods.CallNextHookEx(_kbHook, nCode, wParam, lParam);

                switch (vk)
                {
                    case NativeMethods.VK_CONTROL:
                    case NativeMethods.VK_LCONTROL:
                    case NativeMethods.VK_RCONTROL:
                        // 传真实 vk：状态机要靠它区分“同一个键的另一侧码”（忽略）
                        // 与“另一个触发键”（组合操作 → 作废本次唤出）。
                        result = _machine.CtrlEvent(isDown, vk);
                        break;

                    case NativeMethods.VK_LWIN:
                    case NativeMethods.VK_RWIN:
                        result = _machine.WinEvent(isDown, vk);
                        break;

                    case NativeMethods.VK_ESCAPE:
                        if (isDown) result = _machine.EscapeDown();
                        break;

                    case NativeMethods.VK_MENU:
                    case NativeMethods.VK_LMENU:
                    case NativeMethods.VK_RMENU:
                        result = _machine.AltEvent(isDown, vk);
                        break;

                    default:
                        // 自定义触发键走自己的分支：它们需要在**松开**时结束手势，
                        // 而普通按键只在按下时有意义（OtherKeyDown 会把它当成组合键/取消）。
                        if (_machine.IsCustomTriggerKey(vk))
                            result = _machine.CustomKeyEvent(vk, isDown);
                        else if (isDown)
                            result = _machine.OtherKeyDown();
                        break;
                }

                ApplyEffects(result.Effects);
            }
            catch (Exception ex)
            {
                // 日志只是入队（后台批量落盘），可以直接写，不必再绕一趟 UI
                Logger.Error("键盘钩子异常", ex);
            }
            finally
            {
                // 注意：这里绝不能拼字符串（每次回调都会执行），只传原始数值
                TrackHookDuration(TicksToMs(Stopwatch.GetTimestamp() - start), isMouse: false, msg);
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

                SyncSettings();

                if (_paused || _machine.Paused)
                    return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);

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
                Logger.Error("鼠标钩子异常", ex);
            }
            finally
            {
                // 同上：这里每次鼠标移动都会走到，拼字符串等于给 GC 上强度
                TrackHookDuration(TicksToMs(Stopwatch.GetTimestamp() - start), isMouse: true, msg);
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
        //
        // 本方法只在钩子线程被调用（钩子回调 / WM_TIMER / 回投裁决），
        // 所以它内部可以放心读写钩子线程独占的字段。

        /// <summary>
        /// 把状态机返回的副作用落实到真实世界。
        ///
        /// <para>可能从钩子回调的栈上被调用，因此必须保持轻量：合成输入一律经
        /// <see cref="QueueAction"/> 排到回调返回之后，绝不能当场发送（会重入钩子）。</para>
        /// </summary>
        private void ApplyEffects(HookEffect effects)
        {
            if ((effects & HookEffect.StopHoldTimer) != 0) KillHoldTimer();

            if ((effects & HookEffect.StartHoldTimer) != 0) StartHoldTimer();

            if ((effects & HookEffect.ReplayWinTap) != 0)
            {
                // 轻点：延迟补发完整的按下+松开，让系统原有行为照常发生
                QueueAction(() =>
                {
                    KeySequence.KeyDown(NativeMethods.VK_LWIN);
                    KeySequence.KeyUp(NativeMethods.VK_LWIN);
                });
            }

            if ((effects & HookEffect.ReplayWinDown) != 0)
                QueueAction(() => KeySequence.KeyDown(NativeMethods.VK_LWIN));

            if ((effects & HookEffect.ReplayWinUp) != 0)
                QueueAction(() => KeySequence.KeyUp(NativeMethods.VK_LWIN));

            if ((effects & HookEffect.InjectMiddleUp) != 0) InjectMiddleUp();

            if ((effects & HookEffect.EvaluateTrigger) != 0) EvaluateTrigger();

            if ((effects & HookEffect.ShowWheel) != 0) ShowWheel();

            if ((effects & HookEffect.HideWheel) != 0) PostCancel();

            if ((effects & HookEffect.HideWheelAndExecute) != 0) PostRelease();
        }

        /// <summary>
        /// 把 UI 侧的新设置同步到状态机。只允许在钩子线程调用。
        ///
        /// <para>代价是一次引用比较：设置没变就什么都不做。设置变了就整份覆盖 ——
        /// 状态机宁可看到“稍微旧一点的一整份设置”，也不要看到半新半旧的组合。</para>
        /// </summary>
        private void SyncSettings()
        {
            var settings = _settings;
            if (!ReferenceEquals(settings, _appliedSettings))
            {
                _appliedSettings = settings;

                _machine.ListenCtrl = settings.ListenCtrl;
                _machine.ListenWin = settings.ListenWin;
                _machine.ListenAlt = settings.ListenAlt;
                _machine.ListenMiddle = settings.ListenMiddle;
                _machine.CustomTriggerKeys = settings.CustomTriggerVks;
                _machine.SuppressWin = settings.SuppressWin;
                _machine.SuppressMiddle = settings.SuppressMiddle;
                _machine.CancelOnOtherKey = settings.CancelOnOtherKey;
                _machine.CancelOnLeftClick = settings.CancelOnLeftClick;
                _machine.HoldThresholdMs = settings.HoldThresholdMs;
            }

            // 暂停也走同一条路：置位已在 UI 侧立即生效（回调第一件事就看 _paused），
            // 状态清理留给钩子线程 —— 那些字段是它独占的。
            bool paused = _paused;
            if (paused == _machinePaused) return;

            _machinePaused = paused;

            if (paused) ApplyEffects(_machine.Pause());
            else _machine.Resume();
        }

        /// <summary>
        /// 环境判定。判定本身要读前台窗口/进程，交给 UI 线程做（那里有现成的检测服务与缓存）；
        /// 结果再回投钩子线程裁决 —— 判定期间用户可能已经松手，交给状态机复核才不会出现
        /// “轮盘闪一下就没了”。
        /// </summary>
        private void EvaluateTrigger()
        {
            Post(() =>
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

                PostToHookThread(HookMessage.TriggerResolved, allowed ? new IntPtr(1) : IntPtr.Zero);
            });
        }

        /// <summary>唤出轮盘。状态机要求它离开钩子回调栈，所以推给 UI 线程执行。</summary>
        private void ShowWheel()
        {
            Post(() =>
            {
                try
                {
                    OnTrigger?.Invoke();
                }
                catch (Exception ex)
                {
                    Logger.Error("唤出轮盘失败", ex);

                    // _fired 是钩子线程的状态，回投给它自己去回滚
                    PostToHookThread(HookMessage.RollbackFired);
                }
            });
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

        /// <summary>取消：隐藏轮盘，不执行任何功能。运行在钩子线程，所以只能投递回 UI。</summary>
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

        /// <summary>松开执行：隐藏轮盘并执行选中项。运行在钩子线程，所以只能投递回 UI。</summary>
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
        /// 阈值到达：由钩子线程的 WM_TIMER 调用，运行在它自己的消息泵里（不在钩子回调内）。
        /// 状态机因此可以放心返回 <see cref="HookEffect.EvaluateTrigger"/>，把环境判定交给后续步骤。
        /// </summary>
        private void OnHoldElapsed() => ApplyEffects(_machine.HoldElapsed());

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            var thread = _hookThread;
            if (thread == null) return;
            _hookThread = null;

            // 收尾整段交给钩子线程：要补的残留按键标志归它独占，
            // 摘钩子也只能在安装它的那个线程上做。
            PostToHookThread(HookMessage.Shutdown);

            if (thread.Join(2000))
                Logger.Info("钩子线程已正常收工");
            else
                Logger.Warn("钩子线程未在 2 秒内退出，可能有回调被阻塞");

            _hookThreadId = 0;
        }
    }
}
