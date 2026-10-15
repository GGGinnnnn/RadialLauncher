using RadialLauncher.Services;
using Xunit;

namespace RadialLauncher.Tests;

/// <summary>
/// HookGestureMachine —— “按住 Ctrl / Win / 中键唤出轮盘”的手势状态机。
///
/// 这些逻辑原先只存在于钩子回调里，一行测试都没有，而最容易出错的恰恰是它的边界行为
/// （Win 键的吞与补发、中键接管的补发、轻点与长按的分叉）—— 历史上也确实踩过
/// “双份 KeyUp”“浏览器卡在中键滚动模式”这类坑。现在把它变成可回归的断言。
///
/// 全程不碰 Win32、不碰计时器、不碰 WPF：状态机只回答两件事 ——
/// 这次事件吞不吞（<see cref="HookResult.Swallow"/>）、宿主该做什么（<see cref="HookResult.Effects"/>）。
/// </summary>
public class HookGestureMachineTests
{
    // ==================== 测试脚手架 ====================

    private static bool Has(HookEffect effects, HookEffect flag) => (effects & flag) != 0;

    private static HookPoint P(int x, int y) => new(x, y);

    /// <summary>默认只监听 Ctrl（和出厂配置一致）：Win / Alt / 中键 / 自定义键要显式打开。</summary>
    private static HookGestureMachine Machine(
        bool win = false,
        bool middle = false,
        bool suppressWin = true,
        bool suppressMiddle = true,
        bool alt = false,
        int[]? customKeys = null)
        => new()
        {
            ListenCtrl = true,
            ListenWin = win,
            ListenAlt = alt,
            ListenMiddle = middle,
            CustomTriggerKeys = customKeys ?? [],
            SuppressWin = suppressWin,
            SuppressMiddle = suppressMiddle,
        };

    /// <summary>
    /// 装一个假的“真实按键状态”探针：只有列出的虚拟键算按着。
    /// 状态机本身零 I/O，物理状态由宿主注入 —— 测试因此可以精确复现“同时按住多个键”。
    /// </summary>
    private static void HoldKeys(HookGestureMachine m, params int[] downVks)
    {
        var set = new HashSet<int>(downVks);
        m.IsVirtualKeyDown = vk => set.Contains(vk);
    }

    /// <summary>走完“按住 → 阈值到达 → 环境允许 → 轮盘已显示”这条最常用的路径。</summary>
    private static HookEffect ShowWheelByHolding(HookGestureMachine m, TriggerKind kind)
    {
        switch (kind)
        {
            case TriggerKind.Ctrl: m.CtrlEvent(true); break;
            case TriggerKind.Win: m.WinEvent(true); break;
            case TriggerKind.Alt: m.AltEvent(true); break;
            case TriggerKind.Middle: m.MiddleButtonDown(P(0, 0)); break;
            case TriggerKind.Custom: m.CustomKeyEvent(0x7C, true); break;   // 0x7C = F13
        }

        m.HoldElapsed();
        return m.ResolveTrigger(true);
    }

    // ==================== Ctrl ====================

    [Fact]
    public void CtrlDown_ArmsAndStartsTimer()
    {
        var m = Machine();

        var r = m.CtrlEvent(true);

        Assert.False(r.Swallow);                         // Ctrl 只观察，不拦截
        Assert.True(Has(r.Effects, HookEffect.StartHoldTimer));
        Assert.Equal(TriggerKind.Ctrl, m.ArmedKind);
        Assert.False(m.IsFired);
    }

    [Fact]
    public void CtrlDown_WhenListenDisabled_IsIgnored()
    {
        var m = Machine();
        m.ListenCtrl = false;

        var r = m.CtrlEvent(true);

        Assert.Equal(HookEffect.None, r.Effects);
        Assert.False(m.IsArmed);
    }

    /// <summary>
    /// 已武装 Win 时按下 Ctrl：**另一个触发键**按下 ⇒ 组合操作，本次手势作废。
    ///
    /// <para>旧行为是“第一个按下的赢”静默忽略，于是“先按住 Win 再按住 Ctrl”会留下一个
    /// 仍然武装着的 Win 手势 —— 阈值一到照样弹出轮盘（用户要的是 Ctrl+Win 组合）。
    /// 现在它按组合键处理：取消计时，并补发被吞掉的 Win 按下。</para>
    /// </summary>
    [Fact]
    public void CtrlDown_WhileWinArmed_VoidsTheGestureAsCombo()
    {
        var m = Machine(win: true);
        m.WinEvent(true);

        var r = m.CtrlEvent(true);

        Assert.True(Has(r.Effects, HookEffect.StopHoldTimer));
        Assert.True(Has(r.Effects, HookEffect.ReplayWinDown));      // Win 组合键要能生效
        Assert.False(Has(m.HoldElapsed(), HookEffect.EvaluateTrigger));
    }

    [Fact]
    public void CtrlUp_WithoutArming_DoesNothing()
    {
        var m = Machine();

        var r = m.CtrlEvent(false);

        Assert.Equal(HookEffect.None, r.Effects);
    }

    [Fact]
    public void CtrlUp_EndsGestureWithoutExecuting()
    {
        var m = Machine();
        m.CtrlEvent(true);

        var r = m.CtrlEvent(false);

        Assert.False(r.Swallow);
        Assert.True(Has(r.Effects, HookEffect.StopHoldTimer));
        Assert.False(Has(r.Effects, HookEffect.HideWheelAndExecute));   // 没唤出过就不执行
        Assert.False(m.IsArmed);
    }

    [Fact]
    public void CtrlUp_WhileWinArmed_DoesNothing()
    {
        var m = Machine(win: true);
        m.WinEvent(true);

        var r = m.CtrlEvent(false);

        Assert.Equal(HookEffect.None, r.Effects);
        Assert.Equal(TriggerKind.Win, m.ArmedKind);
    }

    [Fact]
    public void CtrlUp_AfterWheelShown_RequestsExecute()
    {
        var m = Machine();
        m.CtrlEvent(true);
        m.HoldElapsed();
        m.ResolveTrigger(true);

        var r = m.CtrlEvent(false);

        Assert.False(r.Swallow);
        Assert.True(Has(r.Effects, HookEffect.HideWheelAndExecute));
    }

    // ==================== Win：吞与补发 ====================

    [Fact]
    public void WinDown_WithSuppress_SwallowsAndArms()
    {
        var m = Machine(win: true);

        var r = m.WinEvent(true);

        Assert.True(r.Swallow);                          // 吞掉，否则开始菜单会弹出来
        Assert.True(Has(r.Effects, HookEffect.StartHoldTimer));
        Assert.Equal(TriggerKind.Win, m.ArmedKind);
    }

    [Fact]
    public void WinDown_WithoutSuppress_ArmsButPassesThrough()
    {
        var m = Machine(win: true, suppressWin: false);

        var r = m.WinEvent(true);

        Assert.False(r.Swallow);                         // 不干预系统行为
        Assert.True(Has(r.Effects, HookEffect.StartHoldTimer));

        var up = m.WinEvent(false);
        Assert.False(up.Swallow);
        Assert.Equal(HookEffect.StopHoldTimer, up.Effects);
    }

    [Fact]
    public void WinTap_ReplaysFullPressAndRelease()
    {
        var m = Machine(win: true);
        m.WinEvent(true);

        var r = m.WinEvent(false);

        Assert.True(r.Swallow);
        Assert.True(Has(r.Effects, HookEffect.ReplayWinTap));   // 轻点 → 补发按下+松开
    }

    [Fact]
    public void WinCombo_ReplaysWinDownOnFirstOtherKey()
    {
        var m = Machine(win: true);
        m.WinEvent(true);

        var r = m.OtherKeyDown();                        // 例如 Win+E

        Assert.True(Has(r.Effects, HookEffect.ReplayWinDown));  // 组合键必须让系统收到 Win
        Assert.True(Has(r.Effects, HookEffect.StopHoldTimer));  // 同时取消本次唤出
    }

    [Fact]
    public void WinCombo_ReplaysWinDownOnlyOnce()
    {
        var m = Machine(win: true);
        m.WinEvent(true);
        m.OtherKeyDown();

        var r = m.OtherKeyDown();

        Assert.False(Has(r.Effects, HookEffect.ReplayWinDown)); // 第二次不再补发
    }

    [Fact]
    public void WinCombo_ReplayedWin_ReleaseIsNotSwallowedNorTapped()
    {
        var m = Machine(win: true);
        m.WinEvent(true);
        m.OtherKeyDown();

        var r = m.WinEvent(false);

        Assert.False(r.Swallow);                                 // 系统收过 Win 按下，松开要放行
        Assert.False(Has(r.Effects, HookEffect.ReplayWinTap));    // 不能再补一次，否则双份按键
    }

    [Fact]
    public void WinCombo_AfterWheelShown_OtherKeyCancelsWheel()
    {
        var m = Machine(win: true);
        m.WinEvent(true);
        m.HoldElapsed();
        m.ResolveTrigger(true);

        var r = m.OtherKeyDown();

        Assert.True(Has(r.Effects, HookEffect.HideWheel));
    }

    /// <summary>
    /// 轮盘显示期间按了别的键 → 取消并补发 Win 按下（组合键要生效）。此后用户松开 Win，
    /// 这次**真实松开必须放行**：系统那边已经收到了我们补发的 Win 按下，再吞掉松开
    /// 就会让系统以为 Win 一直按着（开始菜单/组合键全部卡住）。
    /// </summary>
    [Fact]
    public void WinCombo_AfterReplayedDown_RealReleaseIsNotSwallowed()
    {
        var m = Machine(win: true);
        m.WinEvent(true);                    // 吞下真实按下
        m.HoldElapsed();
        m.ResolveTrigger(true);              // 轮盘显示
        m.OtherKeyDown();                    // 补发 Win down（组合键）

        var up = m.WinEvent(false);

        Assert.False(up.Swallow);                             // ← 不能吞
        Assert.False(Has(up.Effects, HookEffect.ReplayWinTap));
        Assert.Equal(HookEffect.None, m.ComputeAbortEffects());  // 收尾时也不该再有残留 Win
    }

    [Fact]
    public void WinHeld_WithSuppress_SwallowsReleaseAndExecutes()
    {
        var m = Machine(win: true);
        m.WinEvent(true);
        m.HoldElapsed();
        m.ResolveTrigger(true);

        var r = m.WinEvent(false);

        Assert.True(r.Swallow);
        Assert.True(Has(r.Effects, HookEffect.HideWheelAndExecute));
        Assert.False(Has(r.Effects, HookEffect.ReplayWinTap));
    }

    [Fact]
    public void WinEvent_WhenListenerOffAndNothingSwallowed_IsIgnored()
    {
        var m = Machine(win: false);

        Assert.Equal(HookEffect.None, m.WinEvent(true).Effects);
        Assert.Equal(HookEffect.None, m.WinEvent(false).Effects);
    }

    /// <summary>
    /// 监听被中途关掉、但此前已经吞过一次 Win 按下 —— 这次松开仍必须补发，
    /// 否则系统会以为 Win 一直按着（这条路径以前只靠“信念来源唯一”的注释守着）。
    /// </summary>
    [Fact]
    public void WinUp_WhenListenerTurnedOffMidGesture_StillReplaysWinUp()
    {
        var m = Machine(win: true);
        m.WinEvent(true);                    // 吞下 Win 按下
        m.ResolveTrigger(true);              // 唤出轮盘
        m.RightButtonDown();                 // 取消轮盘：_winSwallowed 仍留着（取消不负责清它）
        m.ListenWin = false;

        var r = m.WinEvent(false);

        Assert.True(r.Swallow);
        Assert.True(Has(r.Effects, HookEffect.ReplayWinUp));
    }

    [Fact]
    public void WinUp_WhenNotArmedAndNotSwallowed_DoesNothing()
    {
        var m = Machine(win: true);

        var r = m.WinEvent(false);

        Assert.Equal(HookEffect.None, r.Effects);
    }

    // ==================== Alt ====================

    [Fact]
    public void AltDown_ArmsAndStartsTimer()
    {
        var m = Machine(alt: true);

        var r = m.AltEvent(true);

        Assert.False(r.Swallow);                          // Alt 只观察，不拦截（Alt+Tab / Alt+F4 必须能用）
        Assert.True(Has(r.Effects, HookEffect.StartHoldTimer));
        Assert.Equal(TriggerKind.Alt, m.ArmedKind);
    }

    [Fact]
    public void AltDown_WhenListenDisabled_IsIgnored()
    {
        var m = Machine();

        var r = m.AltEvent(true);

        Assert.Equal(HookEffect.None, r.Effects);
        Assert.False(m.IsArmed);
    }

    /// <summary>
    /// Alt 监听关闭时，Alt 按下仍要按“其他键”处理：按住 Ctrl 期间按下 Alt
    /// （Ctrl+Alt+Del 的第一下）必须取消本次唤出 —— 这是 v0.4 的既有语义，
    /// 加 Alt 触发键时不能把它放宽。
    /// </summary>
    [Fact]
    public void AltDown_WhileListenDisabled_CancelsPendingTrigger()
    {
        var m = Machine(alt: false);
        m.CtrlEvent(true);

        var r = m.AltEvent(true);

        Assert.True(Has(r.Effects, HookEffect.StopHoldTimer));
        Assert.False(Has(m.HoldElapsed(), HookEffect.EvaluateTrigger));
    }

    [Fact]
    public void AltUp_WhileListenDisabled_DoesNothing()
    {
        var m = Machine(alt: false);

        var r = m.AltEvent(false);

        Assert.Equal(HookEffect.None, r.Effects);
    }

    /// <summary>同 Ctrl：第二个触发键按下 ⇒ 组合操作，作废本次唤出。</summary>
    [Fact]
    public void AltDown_WhileWinArmed_VoidsTheGestureAsCombo()
    {
        var m = Machine(alt: true, win: true);
        m.WinEvent(true);

        var r = m.AltEvent(true);

        Assert.True(Has(r.Effects, HookEffect.StopHoldTimer));
        Assert.False(Has(m.HoldElapsed(), HookEffect.EvaluateTrigger));
    }

    [Fact]
    public void AltUp_WithoutArming_DoesNothing()
    {
        Assert.Equal(HookEffect.None, Machine(alt: true).AltEvent(false).Effects);
    }

    [Fact]
    public void AltUp_WhileCtrlArmed_DoesNothing()
    {
        var m = Machine(alt: true);
        m.CtrlEvent(true);

        var r = m.AltEvent(false);

        Assert.Equal(HookEffect.None, r.Effects);
        Assert.Equal(TriggerKind.Ctrl, m.ArmedKind);
    }

    [Fact]
    public void AltTap_EndsGestureWithoutExecuting()
    {
        var m = Machine(alt: true);
        m.AltEvent(true);

        var r = m.AltEvent(false);

        Assert.False(r.Swallow);
        Assert.True(Has(r.Effects, HookEffect.StopHoldTimer));
        Assert.False(Has(r.Effects, HookEffect.HideWheelAndExecute));
        Assert.False(m.IsArmed);
    }

    [Fact]
    public void AltHeld_AfterWheelShown_ReleaseExecutes()
    {
        var m = Machine(alt: true);
        ShowWheelByHolding(m, TriggerKind.Alt);

        Assert.True(m.IsFired);

        var r = m.AltEvent(false);

        Assert.True(Has(r.Effects, HookEffect.HideWheelAndExecute));
        Assert.False(r.Swallow);
    }

    [Fact]
    public void AltCombo_OtherKey_CancelsPendingTrigger()
    {
        // Alt+F4 这类组合键：Alt 已被武装，按下 F4 必须取消唤出（F4 本身照常传递）
        var m = Machine(alt: true);
        m.AltEvent(true);

        var r = m.OtherKeyDown();

        Assert.True(Has(r.Effects, HookEffect.StopHoldTimer));
        Assert.False(Has(m.HoldElapsed(), HookEffect.EvaluateTrigger));
    }

    [Fact]
    public void AltDown_WhileCustomKeyArmed_VoidsTheGestureAsCombo()
    {
        var m = Machine(alt: true, customKeys: [0x7C]);
        m.CustomKeyEvent(0x7C, true);

        var r = m.AltEvent(true);

        Assert.True(Has(r.Effects, HookEffect.StopHoldTimer));
        Assert.False(Has(m.HoldElapsed(), HookEffect.EvaluateTrigger));
    }

    // ==================== 自定义触发键 ====================

    [Fact]
    public void CustomKey_NotConfigured_IsNotATrigger()
    {
        var m = Machine(customKeys: [0x7C]);

        Assert.True(m.IsCustomTriggerKey(0x7C));
        Assert.False(m.IsCustomTriggerKey(0x7D));
    }

    [Fact]
    public void CustomKeyDown_ArmsAndNeverSwallows()
    {
        var m = Machine(customKeys: [0x7C]);

        var r = m.CustomKeyEvent(0x7C, true);

        Assert.False(r.Swallow);                          // 纯监听：该键原有行为必须照常发生
        Assert.True(Has(r.Effects, HookEffect.StartHoldTimer));
        Assert.Equal(TriggerKind.Custom, m.ArmedKind);
        Assert.False(m.IsFired);
    }

    [Fact]
    public void CustomKeyDown_WhileCtrlArmed_VoidsTheGestureAsCombo()
    {
        var m = Machine(customKeys: [0x7C]);
        m.CtrlEvent(true);

        var r = m.CustomKeyEvent(0x7C, true);

        Assert.True(Has(r.Effects, HookEffect.StopHoldTimer));
        Assert.False(Has(m.HoldElapsed(), HookEffect.EvaluateTrigger));
    }

    [Fact]
    public void CustomKeyTap_EndsGestureWithoutExecuting()
    {
        var m = Machine(customKeys: [0x7C]);
        m.CustomKeyEvent(0x7C, true);

        var r = m.CustomKeyEvent(0x7C, false);

        Assert.False(r.Swallow);
        Assert.True(Has(r.Effects, HookEffect.StopHoldTimer));
        Assert.False(Has(r.Effects, HookEffect.HideWheelAndExecute));
        Assert.False(m.IsArmed);
    }

    [Fact]
    public void CustomKeyHeld_AfterWheelShown_ReleaseExecutes()
    {
        var m = Machine(customKeys: [0x7C]);
        ShowWheelByHolding(m, TriggerKind.Custom);

        Assert.True(m.IsFired);

        var r = m.CustomKeyEvent(0x7C, false);

        Assert.True(Has(r.Effects, HookEffect.HideWheelAndExecute));
        Assert.False(r.Swallow);
    }

    /// <summary>
    /// 松开“另一个”自定义键不能结束当前手势 —— 否则先按住 F13、再顺手摸一下 Shift，
    /// 轮盘就会提前收掉并触发执行。
    /// </summary>
    [Fact]
    public void CustomKeyUp_ForDifferentKey_KeepsGestureArmed()
    {
        var m = Machine(customKeys: [0x7C, 0x10]);        // F13 与 Shift
        m.CustomKeyEvent(0x7C, true);

        var r = m.CustomKeyEvent(0x10, false);

        Assert.Equal(HookEffect.None, r.Effects);
        Assert.Equal(TriggerKind.Custom, m.ArmedKind);
        Assert.True(Has(m.HoldElapsed(), HookEffect.EvaluateTrigger));
    }

    /// <summary>
    /// 已经有一个触发键在武装时，第二个自定义键按下不参与 —— 与 Ctrl / Win / Alt / 中键一致，
    /// 仍然是“第一个按下的赢”，而不是把当前手势取消掉。
    /// </summary>
    /// <summary>两个自定义触发键同时按住 ⇒ 组合操作，作废本次唤出。</summary>
    [Fact]
    public void CustomKeyDown_SecondKeyWhileArmed_VoidsTheGestureAsCombo()
    {
        var m = Machine(customKeys: [0x7C, 0x10]);
        m.CustomKeyEvent(0x7C, true);

        var r = m.CustomKeyEvent(0x10, true);

        Assert.True(Has(r.Effects, HookEffect.StopHoldTimer));
        Assert.False(Has(m.HoldElapsed(), HookEffect.EvaluateTrigger));
    }

    /// <summary>被忽略的那次按下，它的松开也不能结束当前手势。</summary>
    [Fact]
    public void CustomKeyUp_ForIgnoredKey_KeepsGestureArmed()
    {
        var m = Machine(customKeys: [0x7C, 0x10]);
        m.CustomKeyEvent(0x7C, true);
        m.CustomKeyEvent(0x10, true);        // 被忽略

        var r = m.CustomKeyEvent(0x10, false);

        Assert.Equal(HookEffect.None, r.Effects);
        Assert.Equal(TriggerKind.Custom, m.ArmedKind);
    }

    [Fact]
    public void CustomKey_ResetState_ClearsArmedKeySoNextGestureWorks()
    {
        var m = Machine(customKeys: [0x7C]);
        m.CustomKeyEvent(0x7C, true);
        m.ResetState();

        Assert.False(m.IsArmed);

        // 重新武装一个“别的”自定义键：不能被上一轮的残留键码挡住
        m.CustomKeyEvent(0x10, true);
        Assert.Equal(TriggerKind.Custom, m.ArmedKind);
        Assert.True(Has(m.HoldElapsed(), HookEffect.EvaluateTrigger));
    }

    [Fact]
    public void CustomKey_ComboWithOtherKey_CancelsPendingTrigger()
    {
        var m = Machine(customKeys: [0x10]);              // Shift 作触发键
        m.CustomKeyEvent(0x10, true);

        var r = m.OtherKeyDown();                         // 例如 Shift+A

        Assert.True(Has(r.Effects, HookEffect.StopHoldTimer));
        Assert.False(Has(m.HoldElapsed(), HookEffect.EvaluateTrigger));
    }

    // ==================== 鼠标中键：轻点 / 拖动 / 接管 ====================    // ==================== 鼠标中键：轻点 / 拖动 / 接管 ====================

    [Fact]
    public void MiddleDown_ArmsAndPassesThrough()
    {
        var m = Machine(middle: true);

        var r = m.MiddleButtonDown(P(10, 10));

        Assert.False(r.Swallow);                         // 按下先原样交给系统
        Assert.True(Has(r.Effects, HookEffect.StartHoldTimer));
        Assert.Equal(TriggerKind.Middle, m.ArmedKind);
    }

    [Fact]
    public void MiddleDown_WhenListenDisabled_IsIgnored()
    {
        var m = Machine(middle: false);

        var r = m.MiddleButtonDown(P(10, 10));

        Assert.Equal(HookEffect.None, r.Effects);
        Assert.False(m.IsArmed);
    }

    /// <summary>
    /// 已武装 Win 时按下中键：作废 Win 手势（组合操作），本次不武装中键。
    ///
    /// <para>不补发中键松开：这一次中键是自己按下的、原样放行了，它的真实松开会自然传递；
    /// 人为补一次会把“长按”变成“早点击”。</para>
    /// </summary>
    [Fact]
    public void MiddleDown_WhileWinArmed_VoidsTheGestureWithoutInjectingMiddleUp()
    {
        var m = Machine(middle: true, win: true);
        m.WinEvent(true);

        var r = m.MiddleButtonDown(P(10, 10));

        Assert.True(Has(r.Effects, HookEffect.StopHoldTimer));
        Assert.False(Has(r.Effects, HookEffect.InjectMiddleUp));
        Assert.False(Has(m.HoldElapsed(), HookEffect.EvaluateTrigger));
    }

    [Fact]
    public void MiddleDrag_BeyondThreshold_CancelsTrigger()
    {
        var m = Machine(middle: true);
        m.MiddleButtonDown(P(100, 100));

        var r = m.MouseMove(P(100 + 41, 100));           // 超过 40px 判定为中键滚动

        Assert.True(Has(r.Effects, HookEffect.StopHoldTimer));
        Assert.False(Has(m.HoldElapsed(), HookEffect.EvaluateTrigger));  // 已取消，不再唤出
    }

    [Fact]
    public void MiddleDrag_WithinThreshold_KeepsWaiting()
    {
        var m = Machine(middle: true);
        m.MiddleButtonDown(P(100, 100));

        var r = m.MouseMove(P(110, 100));                // 10px，还不算拖动

        Assert.Equal(HookEffect.None, r.Effects);
        Assert.True(Has(m.HoldElapsed(), HookEffect.EvaluateTrigger));
    }

    [Fact]
    public void MiddleMove_WithoutMiddleHold_DoesNothing()
    {
        var m = Machine(middle: true);

        Assert.Equal(HookEffect.None, m.MouseMove(P(500, 500)).Effects);
    }

    [Fact]
    public void MiddleHold_Takeover_InjectsMiddleUpThenShowsWheel()
    {
        var m = Machine(middle: true);
        m.MiddleButtonDown(P(0, 0));
        m.HoldElapsed();

        var effects = m.ResolveTrigger(true);

        Assert.True(Has(effects, HookEffect.InjectMiddleUp));   // 不补发 → 浏览器会卡在中键滚动
        Assert.True(Has(effects, HookEffect.ShowWheel));
        Assert.True(m.IsFired);
    }

    [Fact]
    public void MiddleHold_TakeoverSwallowsTheRealRelease()
    {
        var m = Machine(middle: true);
        m.MiddleButtonDown(P(0, 0));
        m.ResolveTrigger(true);

        var r = m.MiddleButtonUp();

        Assert.True(r.Swallow);                          // 已经补发过松开，真实松开要吞掉
        Assert.True(Has(r.Effects, HookEffect.HideWheelAndExecute));
    }

    [Fact]
    public void MiddleHold_WithoutSuppress_ShowsWheelWithoutInjection()
    {
        var m = Machine(middle: true, suppressMiddle: false);
        m.MiddleButtonDown(P(0, 0));

        var effects = m.ResolveTrigger(true);

        Assert.True(Has(effects, HookEffect.ShowWheel));
        Assert.False(Has(effects, HookEffect.InjectMiddleUp));  // 按下一直放行，没补发过就不能补

        Assert.False(m.MiddleButtonUp().Swallow);               // 真实松开是系统的
    }

    [Fact]
    public void MiddleTap_ReleasePassesThrough()
    {
        var m = Machine(middle: true);
        m.MiddleButtonDown(P(0, 0));

        var r = m.MiddleButtonUp();                      // 没到阈值就松开

        Assert.False(r.Swallow);                         // 和没装本程序时完全一样
        Assert.True(Has(r.Effects, HookEffect.StopHoldTimer));
        Assert.False(Has(r.Effects, HookEffect.HideWheelAndExecute));
    }

    [Fact]
    public void MiddleUp_AfterTakeoverAndCancel_StillSwallowsRelease()
    {
        var m = Machine(middle: true);
        m.MiddleButtonDown(P(0, 0));
        m.ResolveTrigger(true);                          // 接管，已补发松开
        m.LeftButtonDown();                              // 取消轮盘（不负责清中键标志）

        var r = m.MiddleButtonUp();

        Assert.True(r.Swallow);                          // 否则程序会收到两次中键松开
    }

    /// <summary>
    /// 上一次接管残留的中键标志必须被归位，否则下一次接管就不会补发松开 ——
    /// 这正是“浏览器卡在中键滚动模式”的成因，注释里写着的坑，这里钉住它。
    /// </summary>
    [Fact]
    public void MiddleDown_AfterPreviousTakeover_ResetsStaleFlagsSoNextTakeoverInjects()
    {
        var m = Machine(middle: true);

        m.MiddleButtonDown(P(0, 0));
        m.ResolveTrigger(true);                          // 第一次接管：补发过松开
        m.LeftButtonDown();                              // 取消
        m.CtrlEvent(true);                               // 被别的键武装

        m.MiddleButtonDown(P(0, 0));                     // ← 必须在此归位中键标志

        m.CtrlEvent(false);
        m.MiddleButtonDown(P(0, 0));
        var effects = m.ResolveTrigger(true);

        Assert.True(Has(effects, HookEffect.InjectMiddleUp));
    }

    // ==================== 同时按住多个键（真实按键状态核对） ====================
    //
    // 只看事件顺序有天然缺口：另一个键可能是在触发键**之前**按下的，武装那一刻它的
    // KeyDown 早就过去了。阈值到点时问一次真实按键状态，才能覆盖“先后顺序”这个维度。

    /// <summary>同时按着 Ctrl 与 C：阈值到点不能唤出（Ctrl+C 是复制，不是唤出轮盘）。</summary>
    [Fact]
    public void HoldElapsed_WhenOtherKeyHeldSinceBeforeArming_DoesNotTrigger()
    {
        var m = Machine();
        HoldKeys(m, 0x11, 0x43);          // Ctrl + C，两键都按着

        m.CtrlEvent(true);                 // 武装（C 的 KeyDown 早就过去了，OtherKeyDown 看不见）

        var effects = m.HoldElapsed();

        Assert.False(Has(effects, HookEffect.EvaluateTrigger));
        Assert.True(Has(effects, HookEffect.StopHoldTimer));
    }

    /// <summary>正常单击 Ctrl（没有别的键）仍然要唤出。</summary>
    [Fact]
    public void HoldElapsed_WhenOnlyTriggerKeyHeld_StillTriggers()
    {
        var m = Machine();
        HoldKeys(m, 0x11);                 // 只有 Ctrl 自己

        m.CtrlEvent(true);

        Assert.True(Has(m.HoldElapsed(), HookEffect.EvaluateTrigger));
    }

    /// <summary>
    /// 左 Ctrl 是**泛码 0x11 指向的那个键**，左右控制码在真实状态里同时为真。
    /// 只排除用户实际按下的那一个，会把同一个键的另一侧码当成“别的键”，
    /// 于是 Ctrl 触发键永远唤不出轮盘。
    /// </summary>
    [Fact]
    public void HoldElapsed_WhenOnlyLeftCtrlHeld_IsNotMistakenForAnotherKey()
    {
        var m = Machine();
        HoldKeys(m, 0x11, 0xA2);           // 按住左 Ctrl：泛码与 VK_LCONTROL 都按着

        m.CtrlEvent(true, 0xA2);           // 钩子报告的是具体侧码

        Assert.True(Has(m.HoldElapsed(), HookEffect.EvaluateTrigger));
    }

    /// <summary>自定义触发键是 Shift 时同理：0x10 / 0xA0 / 0xA1 是同一个物理键。</summary>
    [Fact]
    public void HoldElapsed_WhenCustomTriggerIsShift_IsNotMistakenForAnotherKey()
    {
        var m = Machine(customKeys: [0x10]);   // Shift
        HoldKeys(m, 0x10, 0xA1);               // 按住右 Shift

        m.CustomKeyEvent(0xA1, true);

        Assert.True(Has(m.HoldElapsed(), HookEffect.EvaluateTrigger));
    }

    /// <summary>中键长按：中键自己按着不算“别的键”，否则中键永远唤不出轮盘。</summary>
    [Fact]
    public void HoldElapsed_WhenOnlyMiddleButtonHeld_StillTriggers()
    {
        var m = Machine(middle: true);
        HoldKeys(m, 0x04);                 // VK_MBUTTON

        m.MiddleButtonDown(P(0, 0));

        Assert.True(Has(m.HoldElapsed(), HookEffect.EvaluateTrigger));
    }

    /// <summary>按住中键时鼠标左键也按着 → 组合操作，不唤出。</summary>
    [Fact]
    public void HoldElapsed_WhenMiddleAndLeftButtonHeld_DoesNotTrigger()
    {
        var m = Machine(middle: true);
        HoldKeys(m, 0x04, 0x01);           // 中键 + 左键

        m.MiddleButtonDown(P(0, 0));

        Assert.False(Has(m.HoldElapsed(), HookEffect.EvaluateTrigger));
    }

    /// <summary>
    /// 关掉“按下其他键则取消”即回到旧语义：只看事件顺序，物理核对不参与。
    /// 组合键防误触是那个开关的职责，不能绕过它。
    /// </summary>
    [Fact]
    public void HoldElapsed_WhenCancelDisabled_IgnoresHeldKeys()
    {
        var m = Machine();
        m.CancelOnOtherKey = false;
        HoldKeys(m, 0x11, 0x43);           // Ctrl + C 都按着

        m.CtrlEvent(true);

        Assert.True(Has(m.HoldElapsed(), HookEffect.EvaluateTrigger));
    }

    /// <summary>宿主没注入探针（单测默认形态）时退回旧行为，不做物理核对。</summary>
    [Fact]
    public void HoldElapsed_WithoutProbe_KeepsLegacyBehaviour()
    {
        var m = Machine();
        m.CtrlEvent(true);

        Assert.True(Has(m.HoldElapsed(), HookEffect.EvaluateTrigger));
    }

    /// <summary>
    /// 阈值内已因别的键取消过，多键核对不应改变结论（仍不唤出，且不重复补发）。
    /// </summary>
    [Fact]
    public void HoldElapsed_AfterOtherKeyCancelled_KeepsCancelled()
    {
        var m = Machine();
        HoldKeys(m, 0x11, 0x43);
        m.CtrlEvent(true);
        m.OtherKeyDown();                  // 顺序路径先取消并停表

        Assert.False(Has(m.HoldElapsed(), HookEffect.EvaluateTrigger));
    }

    /// <summary>
    /// 武装中键期间又来一次中键按下（没有中间的松开）：不能破坏这次接管。
    /// 若把 _midPassedThrough 清成 false，长按接管就不再补发中键松开，
    /// 浏览器会卡在中键滚动模式 —— 这条断言钉住它。
    /// </summary>
    [Fact]
    public void MiddleDown_RepeatedWhileArmed_KeepsTakeoverIntact()
    {
        var m = Machine(middle: true);
        m.MiddleButtonDown(P(0, 0));
        m.MiddleButtonDown(P(0, 0));      // 重复按下（噪声）

        var effects = m.ResolveTrigger(true);

        Assert.True(Has(effects, HookEffect.ShowWheel));
        Assert.True(Has(effects, HookEffect.InjectMiddleUp));   // ← 仍必须补发
        Assert.True(m.MiddleButtonUp().Swallow);
    }

    // ==================== Esc 与普通键 ====================

    [Fact]
    public void Escape_WhenWheelShown_CancelsAndSwallows()
    {
        var m = Machine();
        ShowWheelByHolding(m, TriggerKind.Ctrl);

        var r = m.EscapeDown();

        Assert.True(r.Swallow);
        Assert.True(Has(r.Effects, HookEffect.HideWheel));
        Assert.False(m.IsFired);
    }

    [Fact]
    public void Escape_WhenWheelNotShown_DoesNothing()
    {
        var m = Machine();
        m.CtrlEvent(true);                               // 武装但没唤出

        Assert.Equal(HookEffect.None, m.EscapeDown().Effects);
        Assert.Equal(HookEffect.None, Machine().EscapeDown().Effects);
    }

    [Fact]
    public void OtherKey_BeforeThreshold_CancelsTrigger()
    {
        var m = Machine();
        m.CtrlEvent(true);

        var r = m.OtherKeyDown();

        Assert.True(Has(r.Effects, HookEffect.StopHoldTimer));
        Assert.False(Has(m.HoldElapsed(), HookEffect.EvaluateTrigger));   // Ctrl+C 之类不该唤出
    }

    [Fact]
    public void OtherKey_BeforeThreshold_IsIgnoredWhenCancelDisabled()
    {
        var m = Machine();
        m.CancelOnOtherKey = false;
        m.CtrlEvent(true);

        var r = m.OtherKeyDown();

        Assert.Equal(HookEffect.None, r.Effects);
        Assert.True(Has(m.HoldElapsed(), HookEffect.EvaluateTrigger));
    }

    [Fact]
    public void OtherKey_WhenIdle_DoesNothing()
    {
        Assert.Equal(HookEffect.None, Machine().OtherKeyDown().Effects);
    }

    // ==================== 鼠标左右键 ====================

    [Fact]
    public void LeftClick_WhenWheelShown_Cancels()
    {
        var m = Machine();
        ShowWheelByHolding(m, TriggerKind.Ctrl);

        var r = m.LeftButtonDown();

        Assert.True(Has(r.Effects, HookEffect.HideWheel));
    }

    /// <summary>“点击执行”模式：左键是选中，钩子不能把它当成取消。</summary>
    [Fact]
    public void LeftClick_WhenWheelShownAndCancelDisabled_KeepsWheel()
    {
        var m = Machine();
        m.CancelOnLeftClick = false;
        ShowWheelByHolding(m, TriggerKind.Ctrl);

        var r = m.LeftButtonDown();

        Assert.Equal(HookEffect.None, r.Effects);
        Assert.True(m.IsFired);
    }

    [Fact]
    public void LeftClick_BeforeThreshold_CancelsTrigger()
    {
        var m = Machine();
        m.CtrlEvent(true);

        var r = m.LeftButtonDown();

        Assert.False(r.Swallow);
        Assert.True(Has(r.Effects, HookEffect.StopHoldTimer));
    }

    [Fact]
    public void RightClick_WhenWheelShown_Cancels()
    {
        var m = Machine();
        ShowWheelByHolding(m, TriggerKind.Ctrl);

        Assert.True(Has(m.RightButtonDown().Effects, HookEffect.HideWheel));
    }

    [Fact]
    public void RightClick_BeforeThreshold_CancelsTrigger()
    {
        var m = Machine();
        m.CtrlEvent(true);

        Assert.True(Has(m.RightButtonDown().Effects, HookEffect.StopHoldTimer));
    }

    [Fact]
    public void RightClick_WhenIdle_DoesNothing()
    {
        Assert.Equal(HookEffect.None, Machine().RightButtonDown().Effects);
    }

    // ==================== 计时 / 暂停 / 重置 ====================

    [Fact]
    public void EffectiveHoldMs_ClampsBelowForty()
    {
        var m = Machine();
        m.HoldThresholdMs = 10;

        Assert.Equal(40, m.EffectiveHoldMs);
    }

    [Fact]
    public void EffectiveHoldMs_UsesConfiguredValue()
    {
        var m = Machine();
        m.HoldThresholdMs = 220;

        Assert.Equal(220, m.EffectiveHoldMs);
    }

    [Fact]
    public void HoldElapsed_WhenNotArmed_OnlyStopsTimer()
    {
        var m = Machine();

        Assert.Equal(HookEffect.StopHoldTimer, m.HoldElapsed());
    }

    [Fact]
    public void HoldElapsed_WhenArmed_RequestsEvaluation()
    {
        var m = Machine();
        m.CtrlEvent(true);

        var effects = m.HoldElapsed();

        Assert.True(Has(effects, HookEffect.StopHoldTimer));
        Assert.True(Has(effects, HookEffect.EvaluateTrigger));
    }

    [Fact]
    public void HoldElapsed_AfterFired_DoesNotEvaluateAgain()
    {
        var m = Machine();
        ShowWheelByHolding(m, TriggerKind.Ctrl);

        Assert.Equal(HookEffect.StopHoldTimer, m.HoldElapsed());
    }

    [Fact]
    public void Pause_StopsTimerAndClearsState()
    {
        var m = Machine();
        m.CtrlEvent(true);

        var effects = m.Pause();

        Assert.True(m.Paused);
        Assert.True(Has(effects, HookEffect.StopHoldTimer));
        Assert.False(m.IsArmed);
        Assert.False(Has(m.HoldElapsed(), HookEffect.EvaluateTrigger));
    }

    [Fact]
    public void Resume_ClearsPausedFlag()
    {
        var m = Machine();
        m.Pause();

        m.Resume();

        Assert.False(m.Paused);
    }

    [Fact]
    public void ResetState_ClearsWinFlagsToo()
    {
        var m = Machine(win: true);
        m.WinEvent(true);

        var effects = m.ResetState();

        Assert.True(Has(effects, HookEffect.StopHoldTimer));
        Assert.Equal(HookEffect.None, m.WinEvent(false).Effects);   // 不会再补发 Win 松开
        Assert.Equal(HookEffect.None, m.ComputeAbortEffects());
    }

    [Fact]
    public void ComputeAbortEffects_ReportsSwallowedWinKey()
    {
        var m = Machine(win: true);
        m.WinEvent(true);

        Assert.True(Has(m.ComputeAbortEffects(), HookEffect.ReplayWinUp));
    }

    [Fact]
    public void ComputeAbortEffects_ReportsHeldMiddleOnlyWhenSuppressing()
    {
        // 中键仍被按着、且我们承诺会吞掉它的松开（SuppressMiddle=true）：
        // 此刻退出必须替用户补发一次松开，否则系统/浏览器会一直以为中键没放。
        var withSuppress = Machine(middle: true);
        withSuppress.MiddleButtonDown(P(0, 0));

        // SuppressMiddle=false 时按下本就一直放行、从未补发，
        // 那是用户真实按着的中键，绝不能替他补一次松开。
        var withoutSuppress = Machine(middle: true, suppressMiddle: false);
        withoutSuppress.MiddleButtonDown(P(0, 0));

        Assert.True(Has(withSuppress.ComputeAbortEffects(), HookEffect.InjectMiddleUp));
        Assert.False(Has(withoutSuppress.ComputeAbortEffects(), HookEffect.InjectMiddleUp));
    }

    [Fact]
    public void ComputeAbortEffects_AfterMiddleTakeover_IsEmpty()
    {
        var m = Machine(middle: true);
        m.MiddleButtonDown(P(0, 0));
        m.ResolveTrigger(true);                          // 接管时已经补发过松开

        Assert.False(Has(m.ComputeAbortEffects(), HookEffect.InjectMiddleUp));
    }

    [Fact]
    public void ComputeAbortEffects_AfterCleanRelease_IsEmpty()
    {
        var m = Machine(win: true);
        m.WinEvent(true);
        m.WinEvent(false);

        Assert.Equal(HookEffect.None, m.ComputeAbortEffects());
    }

    // ==================== 判定竞态（异步化的前置网） ====================

    /// <summary>
    /// 环境判定是异步的：结果回来时手势可能已经结束（用户松手了）。
    /// 这次判定必须作废，否则会出现“轮盘闪一下就没了”。
    /// </summary>
    [Fact]
    public void ResolveTrigger_WhenGestureAlreadyEnded_IsIgnored()
    {
        var m = Machine();
        m.CtrlEvent(true);
        m.HoldElapsed();
        m.CtrlEvent(false);                              // 判定期间用户松开了触发键

        var effects = m.ResolveTrigger(true);

        Assert.Equal(HookEffect.None, effects);
        Assert.False(m.IsFired);
    }

    [Fact]
    public void ResolveTrigger_WhenDenied_TreatsGestureAsTap()
    {
        var m = Machine();
        m.CtrlEvent(true);
        m.HoldElapsed();

        var denied = m.ResolveTrigger(false);
        var release = m.CtrlEvent(false);

        Assert.Equal(HookEffect.None, denied);
        Assert.False(Has(release.Effects, HookEffect.HideWheelAndExecute));  // 按轻点处理
        Assert.True(Has(release.Effects, HookEffect.StopHoldTimer));
    }

    [Fact]
    public void ResolveTrigger_WhenAllowed_ShowsWheelOnce()
    {
        var m = Machine();
        m.CtrlEvent(true);
        m.HoldElapsed();

        Assert.True(Has(m.ResolveTrigger(true), HookEffect.ShowWheel));
        Assert.True(m.IsFired);
        Assert.Equal(HookEffect.None, m.ResolveTrigger(true));   // 第二次不再重复唤出
    }

    [Fact]
    public void RollbackFired_AfterShowFailure_PreventsExecuteOnRelease()
    {
        var m = Machine();
        m.CtrlEvent(true);
        m.ResolveTrigger(true);                          // 判定通过、置 _fired

        m.RollbackFired();                               // 宿主唤出失败 → 回滚

        var release = m.CtrlEvent(false);
        Assert.False(m.IsFired);
        Assert.False(Has(release.Effects, HookEffect.HideWheelAndExecute));
    }
}
