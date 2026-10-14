using System;

namespace RadialLauncher.Services
{
    /// <summary>当前按住的是哪个触发键。</summary>
    public enum TriggerKind
    {
        None,
        Ctrl,
        Win,
        Alt,
        Middle,

        /// <summary>用户自定义的触发键（Shift / F13 / CapsLock 等，见 CustomTriggerKeys）。</summary>
        Custom,
    }

    /// <summary>
    /// 状态机要求宿主要做的副作用。
    ///
    /// 用位标志而不是返回值列表/队列：钩子回调每秒可能跑上百次（每一次鼠标移动都会进来），
    /// 一个值类型就能表达完整结果，没有任何分配，也不会给 GC 添麻烦。
    /// </summary>
    [Flags]
    public enum HookEffect
    {
        None = 0,

        /// <summary>（重新）开始计时；时长取 <see cref="HookGestureMachine.EffectiveHoldMs"/>。</summary>
        StartHoldTimer = 1 << 0,

        /// <summary>停止计时。</summary>
        StopHoldTimer = 1 << 1,

        /// <summary>延迟补发 Win 的“按下+松开”（轻点 Win），让开始菜单的原行为照常发生。</summary>
        ReplayWinTap = 1 << 2,

        /// <summary>延迟补发 Win 的“按下”（用户在按 Win+X 这类组合键）。</summary>
        ReplayWinDown = 1 << 3,

        /// <summary>延迟补发 Win 的“松开”（异常路径兜底，避免系统以为 Win 一直按着）。</summary>
        ReplayWinUp = 1 << 4,

        /// <summary>
        /// 补发一次中键松开。接管中键时必须做，否则程序会一直以为中键按着
        /// （浏览器会卡在中键滚动模式，鼠标一动页面就滚）。
        /// </summary>
        InjectMiddleUp = 1 << 5,

        /// <summary>
        /// 宿主需要做一次环境判定（黑白名单、全屏检测等），完成后把结果交给
        /// <see cref="HookGestureMachine.ResolveTrigger"/>。判定是异步的，所以状态机
        /// 在拿到结果时会再校验一次手势是否还在。
        /// </summary>
        EvaluateTrigger = 1 << 6,

        /// <summary>唤出轮盘。</summary>
        ShowWheel = 1 << 7,

        /// <summary>隐藏轮盘（取消，不执行任何功能）。</summary>
        HideWheel = 1 << 8,

        /// <summary>隐藏轮盘并执行选中项。</summary>
        HideWheelAndExecute = 1 << 9,
    }

    /// <summary>状态机自带的坐标点，避免依赖 Win32 的 POINT（保持纯逻辑、便于单测）。</summary>
    public readonly struct HookPoint
    {
        public HookPoint(int x, int y)
        {
            X = x;
            Y = y;
        }

        public int X { get; }
        public int Y { get; }
    }

    /// <summary>状态机对一次输入事件的答复：要不要吞掉，以及宿主需要做什么。</summary>
    public readonly struct HookResult
    {
        public HookResult(bool swallow, HookEffect effects)
        {
            Swallow = swallow;
            Effects = effects;
        }

        /// <summary>是否吞掉这次事件（让系统看不到它）。</summary>
        public bool Swallow { get; }

        /// <summary>宿主需要施加的副作用。</summary>
        public HookEffect Effects { get; }

        /// <summary>放行且什么都不做。</summary>
        public static HookResult None => default;

        /// <summary>吞掉，但不需要做别的事。</summary>
        public static HookResult SwallowOnly => new(true, HookEffect.None);

        /// <summary>放行，但需要做这些事。</summary>
        public static HookResult Of(HookEffect effects) => new(false, effects);
    }

    /// <summary>
    /// “按住 Ctrl / Win / 鼠标中键唤出轮盘”的手势状态机 —— 纯逻辑。
    ///
    /// <para>**为什么单独抽出来**：这套状态机原来长在 <see cref="InputHookService"/> 里，
    /// 和钩子安装、Win32 输入合成、Dispatcher 计时器、日志全混在一起，于是它一行测试都没有。
    /// 偏偏最容易出问题的是它的边界行为（Win 键的吞与补发、中键接管的补发、轻点与长按的分叉），
    /// 历史上也确实踩过“双份 KeyUp”“浏览器卡在中键滚动模式”这类坑。</para>
    ///
    /// <para>**契约**：本类型不做任何 I/O —— 不碰 Win32、不碰计时器、不碰 UI、不写日志、
    /// 不引用 WPF 或 <see cref="NativeMethods"/>。它只回答两件事：
    /// 这次事件吞不吞（<see cref="HookResult.Swallow"/>），以及宿主该做什么
    /// （<see cref="HookResult.Effects"/>）。时序由宿主驱动：计时到点调
    /// <see cref="HoldElapsed"/>，环境判定有结果后调 <see cref="ResolveTrigger"/>。</para>
    ///
    /// <para>**线程约定（为后续把钩子搬到专用线程预留）**：所有状态都只应由同一个线程触碰，
    /// 也就是「钩子所在的线程」。本类型内部不加锁，也刻意不暴露可变字段。</para>
    /// </summary>
    public sealed class HookGestureMachine
    {
        // ============================ 可配置项 ============================
        // 由主窗口从 AppConfig 同步过来（与原 InputHookService 的同名属性一一对应）。

        /// <summary>是否监听 Ctrl。</summary>
        public bool ListenCtrl { get; set; } = true;

        /// <summary>是否监听 Win。</summary>
        public bool ListenWin { get; set; }

        /// <summary>是否监听 Alt (Menu/LeftAlt)。</summary>
        public bool ListenAlt { get; set; }

        /// <summary>
        /// 自定义触发键的虚拟键码。这些键与 Ctrl / Alt 一样**只观察、不拦截**：
        /// 它们本身就带着应用/系统行为（CapsLock 切换大小写、F1 求助、Shift 参与打字），
        /// 一旦拦截就必须为每一种键补发“轻点/组合键”，代价与风险都远高于收益。
        /// 需要拦截的是 Win 与鼠标中键，它们各有专门的分支。
        /// </summary>
        public int[] CustomTriggerKeys { get; set; } = [];

        /// <summary>是否监听鼠标中键。</summary>
        public bool ListenMiddle { get; set; }

        /// <summary>按住 Win 期间是否吞掉 Win 本身（避免弹出开始菜单）。</summary>
        public bool SuppressWin { get; set; } = true;

        /// <summary>按住中键期间是否吞掉中键（接管时需要补发一次松开）。</summary>
        public bool SuppressMiddle { get; set; } = true;

        /// <summary>按住触发键期间按下其他键时取消唤出（防止影响 Ctrl+C 等组合键）。</summary>
        public bool CancelOnOtherKey { get; set; } = true;

        /// <summary>需要按住多久（毫秒）才唤出轮盘。</summary>
        public int HoldThresholdMs { get; set; } = 220;

        /// <summary>
        /// 轮盘显示时左键是否用来取消。
        /// “松开触发键执行”模式下左键=取消；开启“点击执行”后左键才是选中，
        /// 由轮盘窗口自己的轮询去触发，钩子这边不能把它取消掉。
        /// </summary>
        public bool CancelOnLeftClick { get; set; } = true;

        // ============================ 内部状态 ============================

        private TriggerKind _armed = TriggerKind.None;
        private bool _fired;
        private bool _cancelled;

        /// <summary>本轮自定义手势是由哪个虚拟键武装的（0 = 非自定义手势）。</summary>
        private int _armedCustomVk;

        private bool _winSwallowed;
        private bool _winReplayed;

        /// <summary>中键按下是否已经原样放行给系统（不做拦截）。</summary>
        private bool _midPassedThrough;

        /// <summary>接管中键时是否已经补发过一次“松开”。</summary>
        private bool _midReleaseInjected;

        private HookPoint _midDownPoint;

        private bool _paused;

        /// <summary>中键拖动超过该像素距离即判定为“中键拖动”，不再拦截。</summary>
        private const int MiddleDragCancelPx = 40;

        // ============================ 只读视图（给宿主与测试） ============================

        /// <summary>监听是否已整段挂起（设置面板打开期间）。</summary>
        public bool Paused => _paused;

        /// <summary>轮盘是否正在显示。</summary>
        public bool IsFired => _fired;

        /// <summary>当前是否有触发键处于“按住待判”或“已唤出”状态。</summary>
        public bool IsArmed => _armed != TriggerKind.None;

        /// <summary>当前武装的是哪个触发键。</summary>
        public TriggerKind ArmedKind => _armed;

        /// <summary>实际计时时长：低于 40ms 的阈值没有意义，钳到 40ms。</summary>
        public int EffectiveHoldMs => Math.Max(40, HoldThresholdMs);

        // ============================ 键盘：Ctrl ============================

        /// <summary>
        /// Ctrl 的按下/松开。
        /// 注：Ctrl 一直是放行的（不吞），触发与否只体现在唤出轮盘上。
        /// </summary>
        public HookResult CtrlEvent(bool isDown)
        {
            if (!ListenCtrl) return HookResult.None;

            if (isDown)
            {
                if (_armed != TriggerKind.None) return HookResult.None;
                return HookResult.Of(Arm(TriggerKind.Ctrl));
            }

            if (_armed != TriggerKind.Ctrl) return HookResult.None;
            return HookResult.Of(FinishGesture());
        }

        // ============================ 键盘：Alt ============================

        /// <summary>
        /// Alt 的按下/松开。
        /// 注：与 Ctrl 一样，Alt 也是只观察不拦截（单独按 Alt 本来就没有系统行为，且 Alt+F4、Alt+Tab 等组合键必须保持可用）。
        /// </summary>
        public HookResult AltEvent(bool isDown)
        {
            if (!ListenAlt)
            {
                // 监听关闭 ≠ Alt 从此不参与判定：在 v0.4 里 Alt 走的是“其他键”分支，
                // 按住 Ctrl 期间按下 Alt（Ctrl+Alt+Del 的第一下）会取消本次唤出。
                // 这里必须保留这个语义，否则关掉 Alt 监听反而让行为变松。
                return isDown ? OtherKeyDown() : HookResult.None;
            }

            if (isDown)
            {
                if (_armed != TriggerKind.None) return HookResult.None;
                return HookResult.Of(Arm(TriggerKind.Alt));
            }

            if (_armed != TriggerKind.Alt) return HookResult.None;
            return HookResult.Of(FinishGesture());
        }

        // ============================ 键盘：自定义触发键 ============================

        /// <summary>这个虚拟键是否被配置成自定义触发键。</summary>
        public bool IsCustomTriggerKey(int vk)
        {
            var keys = CustomTriggerKeys;
            for (int i = 0; i < keys.Length; i++)
            {
                if (keys[i] == vk) return true;
            }
            return false;
        }

        /// <summary>
        /// 自定义触发键的按下/松开。
        ///
        /// <para>与 Ctrl / Alt 同为“纯监听”：按键照常传给系统，本状态机只负责计时与唤出。
        /// 因此这里不需要任何补发逻辑，也不存在“吞掉了却没补回来”的残留状态。</para>
        ///
        /// <para>松开时要求**同一个**虚拟键：配置了多个自定义键时，松开另一个键不应该
        /// 结束当前手势（否则先按 F13、再摸一下 Shift 就会把轮盘提前收掉）。</para>
        /// </summary>
        public HookResult CustomKeyEvent(int vk, bool isDown)
        {
            if (isDown)
            {
                // 已经被别的键武装时本键不参与 —— 与 Ctrl / Win / Alt / 中键完全一致：
                // “第一个按下的触发键赢”，免得按住 Ctrl 时再摸到自定义键就把手势取消掉。
                if (_armed != TriggerKind.None) return HookResult.None;

                _armedCustomVk = vk;
                return HookResult.Of(Arm(TriggerKind.Custom));
            }

            if (_armed != TriggerKind.Custom || _armedCustomVk != vk) return HookResult.None;

            _armedCustomVk = 0;
            return HookResult.Of(FinishGesture());
        }

        // ============================ 键盘：Win ============================

        /// <summary>
        /// Win 的按下/松开。
        ///
        /// <para>这里把“监听是否开着”一并接过来判断，是因为两者耦合很紧：
        /// 监听关闭后按下不参与，但**已经吞过的那次按下**仍然必须收到它的松开
        /// （见 <see cref="WinUp"/> 的第一条分支），否则系统会以为 Win 一直按着。</para>
        /// </summary>
        public HookResult WinEvent(bool isDown)
        {
            if (isDown)
            {
                // 已经因为别的键武装了，Win 按下不参与；监听关闭时也不参与
                if (!ListenWin) return HookResult.None;
                return WinDown();
            }

            // 监听被关掉但之前吞过一次按下，补上松开（延迟补发）。
            // 走 WinUp 而不是自己再写一遍补发逻辑：“什么时候该补 up”只能有一个信念来源，
            // 否则很容易变成双份 KeyUp。
            if (ListenWin || _winSwallowed) return WinUp();

            return HookResult.None;
        }

        private HookResult WinDown()
        {
            if (_armed != TriggerKind.None) return HookResult.None;

            var fx = Arm(TriggerKind.Win);
            _winSwallowed = SuppressWin;
            _winReplayed = false;

            return new HookResult(_winSwallowed, fx);
        }

        private HookResult WinUp()
        {
            if (_armed != TriggerKind.Win)
            {
                if (!_winSwallowed) return HookResult.None;

                // 拦截期间监听被关掉等异常路径：补一个 up
                _winSwallowed = false;
                var extra = _winReplayed ? HookEffect.None : HookEffect.ReplayWinUp;
                return new HookResult(true, extra);
            }

            bool fired = _fired;
            var effects = FinishGesture();

            if (_winReplayed)
            {
                // Windows 已经收到过 Win 按下，松开必须放行
                _winSwallowed = false;
                _winReplayed = false;
                return HookResult.Of(effects);
            }

            if (!_winSwallowed) return HookResult.Of(effects);

            _winSwallowed = false;

            if (!fired)
            {
                // 轻点：延迟补发完整的按下+松开，让系统原有行为照常发生
                effects |= HookEffect.ReplayWinTap;
            }

            return new HookResult(true, effects);
        }

        // ============================ 键盘：Esc 与普通键 ============================

        /// <summary>Esc 按下：轮盘显示中用来取消（并吞掉，避免 Esc 漏给下层）。</summary>
        public HookResult EscapeDown()
        {
            if (!_fired) return HookResult.None;

            return new HookResult(true, CancelGesture());
        }

        /// <summary>其他键按下：组合键识别与取消（按键本身始终放行）。</summary>
        public HookResult OtherKeyDown()
        {
            var kind = _armed;
            if (kind == TriggerKind.None) return HookResult.None;

            var effects = HookEffect.None;

            if (_fired)
            {
                // 轮盘显示中又按了别的键 → 取消轮盘，按键继续传递
                effects |= CancelGesture();
            }
            else if (!_cancelled && CancelOnOtherKey)
            {
                // 阈值内按了别的键 → 说明用户在做组合键（Ctrl+C / Win+E）
                _cancelled = true;
                effects |= HookEffect.StopHoldTimer;
            }

            if (kind == TriggerKind.Win && _winSwallowed && !_winReplayed)
            {
                // Windows 还没收到过 Win 按下，先补一下，组合键才能生效
                _winReplayed = true;
                effects |= HookEffect.ReplayWinDown;
            }

            return HookResult.Of(effects);
        }

        // ============================ 鼠标中键 ============================
        //
        // 与 Ctrl / Win 最大的不同：中键按下时**先原样放行**，
        //   · 阈值内松开 → 轻点，什么都不用补，原生行为（如浏览器新标签页）照常；
        //   · 按住超过阈值 → 这时才接管：弹出轮盘，并补一次“松开”，
        //     否则程序会一直以为中键按着（浏览器会卡在中键滚动模式）。

        /// <summary>中键按下：始终放行（不拦截），只负责武装计时。</summary>
        public HookResult MiddleButtonDown(HookPoint pt)
        {
            if (!ListenMiddle) return HookResult.None;

            if (_armed != TriggerKind.None)
            {
                // 上一次手势还没结束（例如接管时补发过松开、但用户又立刻按下中键）就被
                // 别的键武装了。这里必须把中键的两个标志归位，否则这一次真实松开会被
                // 上一次残留的 _midReleaseInjected 误判成“已经补发过”而吞掉，
                // 导致系统以为中键一直按着（浏览器卡在中键滚动模式）。
                _midPassedThrough = false;
                _midReleaseInjected = false;
                return HookResult.None;
            }

            var fx = Arm(TriggerKind.Middle);
            _midDownPoint = pt;
            _midPassedThrough = true;
            _midReleaseInjected = false;

            return HookResult.Of(fx);
        }

        /// <summary>中键松开。</summary>
        /// <returns><see cref="HookResult.Swallow"/> 为 true 表示吞掉这次真实的“中键松开”。</returns>
        public HookResult MiddleButtonUp()
        {
            if (_armed != TriggerKind.Middle)
            {
                // 不是本程序武装的那次中键；如果之前接管时补发过松开，
                // 这次真实松开要吞掉，免得程序收到两次松开。
                if (!_midReleaseInjected) return HookResult.None;

                _midReleaseInjected = false;
                _midPassedThrough = false;
                return HookResult.SwallowOnly;
            }

            bool fired = _fired;

            // 必须在 FinishGesture 之前把值读走：FinishGesture 会把手势内的中键标志
            // 归零（防止渗进下一次手势），之后再读就永远是 false 了。
            bool injected = _midReleaseInjected;

            var effects = FinishGesture();

            // 轻点：原样放行；接管过就吞掉这次真实松开
            return new HookResult(fired && injected, effects);
        }

        /// <summary>鼠标移动：中键按下后明显拖动 → 判定为“中键滚动”，放弃唤出。</summary>
        public HookResult MouseMove(HookPoint pt)
        {
            if (_armed != TriggerKind.Middle || _fired || !_midPassedThrough) return HookResult.None;

            int dx = pt.X - _midDownPoint.X;
            int dy = pt.Y - _midDownPoint.Y;

            // 按下本来就是放行的，这里什么都不用补，原生滚动完全不受影响。
            if (dx * dx + dy * dy >= MiddleDragCancelPx * MiddleDragCancelPx)
            {
                _cancelled = true;
                return HookResult.Of(HookEffect.StopHoldTimer);
            }

            return HookResult.None;
        }

        // ============================ 鼠标左右键 ============================

        /// <summary>
        /// 左键按下：轮盘显示时用来取消；阈值内按下则取消本次唤出。
        /// 但“点击执行”模式下左键是选中，必须放给 WheelWindow 自己的轮询处理。
        /// </summary>
        public HookResult LeftButtonDown()
        {
            if (_fired)
            {
                var fx = CancelOnLeftClick ? CancelGesture() : HookEffect.None;
                return HookResult.Of(fx);
            }

            return CancelPendingTrigger();
        }

        /// <summary>右键按下：始终是取消。</summary>
        public HookResult RightButtonDown()
        {
            if (_fired) return HookResult.Of(CancelGesture());

            return CancelPendingTrigger();
        }

        /// <summary>阈值内按下了左右键 → 取消本次唤出（只是不再唤出，按键照常传递）。</summary>
        private HookResult CancelPendingTrigger()
        {
            if (_armed == TriggerKind.None) return HookResult.None;

            _cancelled = true;
            return HookResult.Of(HookEffect.StopHoldTimer);
        }

        // ============================ 计时与判定 ============================

        /// <summary>
        /// 按住时间到达阈值。宿主由计时器调用。
        ///
        /// <para>需要环境判定时返回 <see cref="HookEffect.EvaluateTrigger"/>，宿主判定完成后
        /// 把结果交回 <see cref="ResolveTrigger"/> —— 判定期间手势可能已经结束，所以这里
        /// 不直接决定“唤出”。</para>
        /// </summary>
        public HookEffect HoldElapsed()
        {
            // 计时到点就一定要停（无论手势还在不在）
            var effects = HookEffect.StopHoldTimer;

            if (_armed == TriggerKind.None || _fired || _cancelled) return effects;

            return effects | HookEffect.EvaluateTrigger;
        }

        /// <summary>
        /// 环境判定的结果回来了。
        ///
        /// <para>本方法会用**当前**状态再校验一次：如果判定期间用户已经松开了触发键
        /// （或者轮盘已经取消/唤出过），这次判定就作废。这是把环境判定挪出钩子线程后
        /// 唯一新增的时序缺口，必须在这里闭合，否则会出现“轮盘闪一下就没了”。</para>
        /// </summary>
        public HookEffect ResolveTrigger(bool allowed)
        {
            if (_armed == TriggerKind.None || _fired || _cancelled) return HookEffect.None;

            if (!allowed)
            {
                // 被黑白名单或全屏检测拦截：按“轻点”处理，保留系统原有按键行为
                _cancelled = true;
                return HookEffect.None;
            }

            var effects = HookEffect.ShowWheel;

            // 中键：按下时已经原样放行给系统了，现在决定接管，必须补一次“松开”。
            // 这行以前只写在 OnHoldElapsed 里，注释也留在那儿（与 Dispose 的判定同源）。
            if (_armed == TriggerKind.Middle && _midPassedThrough && SuppressMiddle && !_midReleaseInjected)
            {
                _midReleaseInjected = true;
                effects |= HookEffect.InjectMiddleUp;
            }

            _fired = true;

            return effects;
        }

        /// <summary>唤出轮盘失败时回滚：否则松开时会走“执行选中项”的路径。</summary>
        public void RollbackFired() => _fired = false;

        // ============================ 暂停 / 重置 ============================

        /// <summary>整段挂起（设置面板打开期间），并清掉手势状态。</summary>
        public HookEffect Pause()
        {
            _paused = true;
            return ResetState();
        }

        public void Resume() => _paused = false;

        /// <summary>只清状态，不补发任何按键。</summary>
        public HookEffect ResetState()
        {
            _armed = TriggerKind.None;
            _fired = false;
            _cancelled = false;
            _armedCustomVk = 0;
            _winSwallowed = false;
            _winReplayed = false;
            _midPassedThrough = false;
            _midReleaseInjected = false;

            return HookEffect.StopHoldTimer;
        }

        /// <summary>
        /// 进程退出前的收尾：报告哪些键是本程序吞下但没补回来的。
        /// 宿主必须在 <see cref="ResetState"/> **之前**调用，因为清理会把这些标志清掉。
        /// </summary>
        public HookEffect ComputeAbortEffects()
        {
            var effects = HookEffect.None;

            if (_winSwallowed && !_winReplayed) effects |= HookEffect.ReplayWinUp;

            // 与 ResolveTrigger 的接管条件保持一致：只有 SuppressMiddle 时我们才会
            // 主动补发过一次中键松开。若 SuppressMiddle 关闭，按下本就一直放行、
            // 从未补发，此时 _midPassedThrough 恒为 true、_midReleaseInjected 恒为
            // false，这里是用户真实按着的中键，绝不能替他补一次松开。
            if (SuppressMiddle && _midPassedThrough && !_midReleaseInjected)
                effects |= HookEffect.InjectMiddleUp;

            return effects;
        }

        // ============================ 内部：手势生命周期 ============================

        /// <summary>武装：开始为某个触发键计时。重复武装会重置判定标志。</summary>
        private HookEffect Arm(TriggerKind kind)
        {
            _armed = kind;
            _fired = false;
            _cancelled = false;

            if (kind != TriggerKind.Custom) _armedCustomVk = 0;

            return HookEffect.StartHoldTimer;
        }

        /// <summary>触发键松开：结束手势；已经唤出过轮盘就请求执行。</summary>
        private HookEffect FinishGesture()
        {
            var effects = HookEffect.StopHoldTimer;

            bool wasFired = _fired;
            _armed = TriggerKind.None;
            _fired = false;
            _cancelled = false;
            _armedCustomVk = 0;

            // 手势只要结束，中键的两个标志就必须归位：调用方（MiddleButtonUp）会先读走
            // 它需要的那份值，这里清掉是为了防止任何残留标志渗进下一次手势。
            _midPassedThrough = false;
            _midReleaseInjected = false;

            if (!wasFired) return effects;

            // ★ 执行动作会 Process.Start / SendInput / 写日志，宿主必须离开钩子回调
            return effects | HookEffect.HideWheelAndExecute;
        }

        /// <summary>取消：隐藏轮盘，不执行任何功能。</summary>
        private HookEffect CancelGesture()
        {
            var effects = HookEffect.StopHoldTimer;

            bool wasFired = _fired;
            _armed = TriggerKind.None;
            _fired = false;
            _cancelled = false;
            _armedCustomVk = 0;

            if (!wasFired) return effects;

            return effects | HookEffect.HideWheel;
        }
    }
}
