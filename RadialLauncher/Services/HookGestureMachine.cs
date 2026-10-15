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

        /// <summary>
        /// 宿主注入的“这个虚拟键此刻是否按着”查询：问的是键鼠的**真实状态**，与事件顺序无关。
        ///
        /// <para>**为什么需要它**：只看事件顺序判断“是不是组合键”有天然缺口 —— 另一个键可能是在
        /// 触发键**之前**就按下的（先按住 Shift 再按 Ctrl），武装那一刻早就错过了它的 KeyDown，
        /// <see cref="OtherKeyDown"/> 永远看不见它，于是阈值一到照样唤出轮盘。
        /// 只有直接问一次真实按键状态，才能覆盖“先后顺序”这个维度。</para>
        ///
        /// <para>宿主注入 <see cref="KeyStateHelper.IsDown"/>（GetAsyncKeyState）。默认 null 表示
        /// 不做物理核对 —— 本类型“纯逻辑、零 I/O”的契约因此得以保持，单测可以直接注入假探针。</para>
        /// </summary>
        public Func<int, bool>? IsVirtualKeyDown { get; set; }

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
        public HookResult CtrlEvent(bool isDown, int vk = VkControl)
        {
            if (!ListenCtrl) return HookResult.None;

            if (isDown)
            {
                // 已经武装着触发键，又按下了**另一个**触发键 —— 两个触发键同时按着，
                // 不做“第一个赢”的静默忽略，而是作废本次手势（见 ComboCancel）。
                // 同一个键的另一侧码（左/右 Ctrl）与重复按下仍按原样忽略。
                if (_armed != TriggerKind.None)
                    return IsArmedTriggerVk(vk) ? HookResult.None : ComboCancel(_armed);

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
        public HookResult AltEvent(bool isDown, int vk = VkMenu)
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
                if (_armed != TriggerKind.None)
                    return IsArmedTriggerVk(vk) ? HookResult.None : ComboCancel(_armed);

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
                // 已经被别的键武装时：同一个键自己（重复按下）忽略；
                // 另一个键按着则判为组合操作并作废本次唤出（与 Ctrl / Win / Alt / 中键一致）。
                if (_armed != TriggerKind.None)
                    return IsArmedTriggerVk(vk) ? HookResult.None : ComboCancel(_armed);

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
        public HookResult WinEvent(bool isDown, int vk = VkLeftWin)
        {
            if (isDown)
            {
                // 已经因为别的键武装了：Win 是“另一个触发键”，本次手势作废（组合键语义）。
                // 这条分支在 ListenWin 之前判断 —— 就算没监听 Win，Ctrl + Win 也是组合操作。
                if (_armed != TriggerKind.None)
                    return IsArmedTriggerVk(vk) ? HookResult.None : ComboCancel(_armed);

                // 监听关闭时不参与
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

                _winSwallowed = false;

                // Windows 已经收到过我们补发的 Win 按下（组合键路径，如 Ctrl+Win）——
                // 这次**真实松开必须放行**。以前这里不分青红皂白地返回 Swallow=true，
                // 于是系统收到了按下却等不到松开，以为 Win 一直按着（开始菜单卡住、
                // 后续所有组合键都变成 Win+…）。这正是“补发了 down 就必须放行 up”的另一半。
                if (_winReplayed)
                {
                    _winReplayed = false;
                    return HookResult.Of(HookEffect.None);
                }

                // 我们吞了按下、却从没补发过（拦截期间监听被关掉等异常路径）：补一个 up
                return new HookResult(true, HookEffect.ReplayWinUp);
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

            effects |= ReplaySwallowedWin(kind);

            return HookResult.Of(effects);
        }

        /// <summary>
        /// 按住触发键期间又按下了**另一个键** → 判定为组合操作，作废本次唤出。
        ///
        /// <para>两条入口共用它：①别的键按下（<see cref="OtherKeyDown"/> 早期就看得见）；
        /// ②**另一个触发键**按下 —— 后者以前是“第一个赢”的静默忽略，于是“先按住 Ctrl 再按住
        /// Win”会留下一个仍然武装着的 Ctrl 手势。现在它同样是组合操作。</para>
        /// </summary>
        /// <param name="kind">武装中的触发键。调用方必须在本次调用**之前**读好它。</param>
        private HookResult ComboCancel(TriggerKind kind)
        {
            // 轮盘已经显示：与“轮盘显示中按下别的键”完全同义 —— 取消，不执行任何功能。
            // （与 OtherKeyDown 一致：这一条不受 CancelOnOtherKey 约束。）
            if (_fired) return HookResult.Of(CancelGesture() | ReplaySwallowedWin(kind));

            // 用户关掉了“按下其他键则取消”：退回旧的“静默忽略第二个键”语义。
            // 组合键防误触是那个开关的职责，这里不能绕过它自作主张。
            if (!CancelOnOtherKey) return HookResult.None;

            _cancelled = true;

            // 若之前吞过 Win 按下，现在必须让系统收到它，组合键才成立（Win+E、Ctrl+Win+…）。
            return HookResult.Of(HookEffect.StopHoldTimer | ReplaySwallowedWin(kind));
        }

        /// <summary>
        /// 把先前吞下的 Win 按下补回给系统（只补一次）。
        ///
        /// <para>收敛成一个出口的理由与 <see cref="WinUp"/> 相同：多一条路径各写一遍，
        /// 迟早变成双份 KeyDown。</para>
        /// </summary>
        private HookEffect ReplaySwallowedWin(TriggerKind kind)
        {
            if (kind != TriggerKind.Win || !_winSwallowed || _winReplayed) return HookEffect.None;

            _winReplayed = true;
            return HookEffect.ReplayWinDown;
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
                // 情况一：武装中键的**就是它自己** —— 重复的按下（中间没有松开）只当噪声。
                // ★ 必须**在任何标志归位之前**返回：把 _midPassedThrough 清成 false 会让
                //   这次合法长按变成“从未接管”，松开时便不再补发中键松开 —— 正是
                //   “浏览器卡在中键滚动模式”那个老坑。
                if (_armed == TriggerKind.Middle) return HookResult.None;

                // 情况二：上一次手势还没结束（例如接管时补发过松开、用户又立刻按下中键）
                // 就被**别的键**武装了。这里必须把中键的两个标志归位，否则这一次真实松开会被
                // 上一次残留的 _midReleaseInjected 误判成“已经补发过”而吞掉。
                _midPassedThrough = false;
                _midReleaseInjected = false;

                // 另一个触发键按着 → 组合操作，作废外面的手势。
                // 注意这里**不补发中键松开**：这一次中键是自己按下的，原样放行给系统，
                // 它的真实松开会走上面的“未接管”路径自然传递（人为补发会把长按变成早点击）。
                return ComboCancel(_armed);
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

            // ★ 阈值到点，先做一次“真实按键状态”核对：只要此刻除了触发键自己还有别的键
            //   （含鼠标键）按着，就认定用户在做组合操作，本次不唤出。
            //
            //   为什么必须问物理状态、而不能只看事件顺序：另一个键可能是在触发键**之前**
            //   按下的，武装那一刻它的 KeyDown 早就过去了 —— 这正是“同时按住多个键仍会
            //   唤出轮盘”的缺口所在。
            if (CancelOnOtherKey && HasOtherKeyDown())
            {
                // 与“别的键按下”那条路径完全同义（含吞过 Win 时的补发），所以直接复用：
                // _cancelled 会让随后回来的判定结果一并作废，松开时也只走“没唤出过”的分支。
                return effects | ComboCancel(_armed).Effects;
            }

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

        // ============================ 内部：多键共存检查 ============================
        //
        // 决定“这次到底算不算组合操作”的只有一处时序信息：事件顺序（OtherKeyDown）。
        // 它漏得掉“别的键先于触发键按下”这一类，所以阈值到点时要再问一次真实按键状态。
        // 探测本身是宿主注入的 I/O（见 IsVirtualKeyDown），本类型只负责“扫哪些键、怎么判”。

        /// <summary>扫描范围：虚拟键 1..254（0 无效、255 无意义）。鼠标键 1..6 也在其中。</summary>
        private const int MinScanVk = 0x01;

        /// <inheritdoc cref="MinScanVk"/>
        private const int MaxScanVk = 0xFE;

        // 与 NativeMethods 的同名常量一致。这里刻意不引用它：本类型的契约是
        // “纯逻辑、零 Win32 依赖”，照抄几个常量比拉进整个 P/Invoke 表划算。
        private const int VkMiddleButton = 0x04;
        private const int VkShift = 0x10;
        private const int VkControl = 0x11;
        private const int VkMenu = 0x12;
        private const int VkLeftWin = 0x5B;
        private const int VkRightWin = 0x5C;
        private const int VkLShift = 0xA0;
        private const int VkRShift = 0xA1;
        private const int VkLControl = 0xA2;
        private const int VkRControl = 0xA3;
        private const int VkLMenu = 0xA4;
        private const int VkRMenu = 0xA5;

        /// <summary>
        /// 此刻除了“当前武装的触发键”之外，是否还有别的键（含鼠标键）按着。
        ///
        /// <para>与顺序无关：无论那个键是在触发键之前还是之后按下，只要核对这一刻它按着，
        /// 就判为组合操作。这正是本方法相对于 <see cref="OtherKeyDown"/> 的全部价值。</para>
        ///
        /// <para>宿主没注入 <see cref="IsVirtualKeyDown"/> 时返回 false：退回“只看事件顺序”的
        /// 老行为，单测里也是默认形态。</para>
        /// </summary>
        private bool HasOtherKeyDown()
        {
            var probe = IsVirtualKeyDown;
            if (probe == null) return false;

            for (int vk = MinScanVk; vk <= MaxScanVk; vk++)
            {
                if (IsArmedTriggerVk(vk)) continue;
                if (probe(vk)) return true;
            }

            return false;
        }

        /// <summary>这个虚拟键是否就是“当前武装的触发键自己” —— 它按着是应该的，不算组合操作。</summary>
        private bool IsArmedTriggerVk(int vk)
        {
            switch (_armed)
            {
                case TriggerKind.Ctrl:
                    return SameKeyFamily(vk, VkControl);

                case TriggerKind.Win:
                    return vk == VkLeftWin || vk == VkRightWin;

                case TriggerKind.Alt:
                    return SameKeyFamily(vk, VkMenu);

                case TriggerKind.Middle:
                    return vk == VkMiddleButton;

                case TriggerKind.Custom:
                    // 自定义键可能是 Shift：泛码 0x10 与左右码 0xA0/0xA1 在 GetAsyncKeyState
                    // 眼里是同一批键（按住左 Shift 时三者都为真）。若只排除用户实际按下的那一个，
                    // 同一个键的另一侧码就会被判成“别的键”，于是 Shift 触发键永远唤不出轮盘。
                    return SameKeyFamily(vk, _armedCustomVk);

                default:
                    return false;
            }
        }

        /// <summary>
        /// 两个虚拟键是否属于同一个“等价家族”。泛修饰键（Shift/Ctrl/Alt）与它左右侧的具体
        /// 键码问的是同一个物理键，必须按家族比较，不能按数字比较。
        /// </summary>
        private static bool SameKeyFamily(int a, int b)
        {
            if (a == b) return true;

            int family = KeyFamilyOf(a);
            return family != 0 && family == KeyFamilyOf(b);
        }

        /// <summary>泛修饰键的家族 id；非修饰键返回 0，表示“各算各的”。</summary>
        private static int KeyFamilyOf(int vk) => vk switch
        {
            VkShift or VkLShift or VkRShift => VkShift,      // 0x10 / 0xA0 / 0xA1
            VkControl or VkLControl or VkRControl => VkControl, // 0x11 / 0xA2 / 0xA3
            VkMenu or VkLMenu or VkRMenu => VkMenu,          // 0x12 / 0xA4 / 0xA5
            _ => 0,
        };

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
