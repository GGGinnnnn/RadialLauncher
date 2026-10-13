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

    /// <summary>默认只监听 Ctrl（和出厂配置一致）：Win / 中键要显式打开。</summary>
    private static HookGestureMachine Machine(
        bool win = false,
        bool middle = false,
        bool suppressWin = true,
        bool suppressMiddle = true)
        => new()
        {
            ListenCtrl = true,
            ListenWin = win,
            ListenMiddle = middle,
            SuppressWin = suppressWin,
            SuppressMiddle = suppressMiddle,
        };

    /// <summary>走完“按住 → 阈值到达 → 环境允许 → 轮盘已显示”这条最常用的路径。</summary>
    private static HookEffect ShowWheelByHolding(HookGestureMachine m, TriggerKind kind)
    {
        switch (kind)
        {
            case TriggerKind.Ctrl: m.CtrlEvent(true); break;
            case TriggerKind.Win: m.WinEvent(true); break;
            case TriggerKind.Middle: m.MiddleButtonDown(P(0, 0)); break;
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

    [Fact]
    public void CtrlDown_WhileOtherTriggerArmed_IsIgnored()
    {
        var m = Machine(win: true);
        m.WinEvent(true);

        var r = m.CtrlEvent(true);

        Assert.Equal(HookEffect.None, r.Effects);
        Assert.Equal(TriggerKind.Win, m.ArmedKind);      // 仍然是 Win 在武装
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

    // ==================== 鼠标中键：轻点 / 拖动 / 接管 ====================

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

    [Fact]
    public void MiddleDown_WhileOtherTriggerArmed_DoesNotArm()
    {
        var m = Machine(middle: true, win: true);
        m.WinEvent(true);

        var r = m.MiddleButtonDown(P(10, 10));

        Assert.Equal(HookEffect.None, r.Effects);
        Assert.Equal(TriggerKind.Win, m.ArmedKind);
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
