# 深度分析：把 WH_KEYBOARD_LL / WH_MOUSE_LL 钩子搬出 UI 线程

> 依据：InputHookService.cs（当前 730 行）、MainWindow.xaml.cs、NativeMethods.cs、WheelWindow.xaml.cs 的现源码逐行核对。
> 本文只做分析与方案设计，不改代码。

> **状态：已实施。** §3 的 ①②③ 已按本方案落地（NativeMethods 增补 → InputHookService 原子重构 → MainWindow 接快照）。
> 实施中与本文有两处偏离，均已在代码注释里写明理由：
> 1. **钩子线程用 `IsBackground = true`**（本文 §3.2-A 原写 false）。理由：背景线程不会在漏掉 `Dispose()`
>    的退出路径上把进程吊住 —— “进程退不掉”正是这个钩子历史上最常见的事故形态；正常退出仍走
>    `WM_APP+Shutdown` → `Join(2000)` 的有序收尾（实测日志：`钩子线程已退出` → `钩子线程已正常收工`）。
> 2. **环境判定用 `Post` 到 UI + 结果经 `WM_APP+TriggerResolved` 回投钩子线程**（即本文 §3.2-G 方案 1），
>    钩子线程侧不等待、不阻塞；`_fired` 由状态机在裁决处理器里置位。
>
> 真机验证（本机实测，非纸面）：Ctrl 长按 → `轮盘已唤出` → Esc → `轮盘已取消`；Win 轻点补发使前台切到
> `SearchHost.exe`（开始菜单被补发出来）；中键长按接管 → `轮盘已唤出`；`--quit` 优雅退出，钩子线程 Join 成功；
> 最长回调耗时 0.53–0.82 ms（改造前日志为 23–37 ms）。

---

## 1. 现状：钩子到底"住在"哪个线程

### 1.1 事实链（每条都有代码位置）

| # | 事实 | 位置 |
|---|---|---|
| 1 | `InputHookService` 构造发生在 UI 线程（`MainWindow` 字段初始化器 `new()`），构造时捕获 `Dispatcher.CurrentDispatcher` | MainWindow.xaml.cs:19，InputHookService.cs:117-119 |
| 2 | `SetWindowsHookExW` 在 UI 线程被调用（`ApplyConfig → Start()`） | InputHookService.cs:196-221，MainWindow.xaml.cs:193-195 |
| 3 | Windows 的 LL 钩子机制：**回调在"安装钩子的那个线程"的消息循环上下文里执行**。安装线程 = UI 线程 → 每一次全局按键、每一次鼠标移动，`KeyboardProc` / `MouseProc` 都在 WPF UI 线程上跑 | NativeMethods.cs:172-173 + 上述 |
| 4 | 手势状态机（`_armed/_fired/_cancelled/_winSwallowed/_winReplayed/_midPassedThrough/_midReleaseInjected`）只在 UI 线程被触碰：钩子回调、`DispatcherTimer.Tick`、`Pause/Resume`、`SyncHookSettings` 全在 UI 线程 | 全文件 |
| 5 | 长按计时用的是 `DispatcherTimer`（UI 线程的消息循环驱动） | InputHookService.cs:123-127, 581-590 |
| 6 | 所有"重活"（执行动作、唤出/隐藏轮盘、环境判定）通过 `Post()` 推回 UI 线程 | InputHookService.cs:141-154, 662-690 |

**结论：今天的线程安全是"偶然的单线程安全"** —— 整个程序恰好共用一个线程，所以状态机不需要锁。这也意味着：把钩子搬走线程这件事，动的不只是"安装位置"，而是**状态机的所有权**。

### 1.2 为什么这是问题（原理 + 日志证据）

1. **回调本身的代码并不慢**。`MouseProc`/`KeyboardProc` 本体只有 `PtrToStructure` + 一个 switch，正常 <1ms。日志里的 23/37/25ms 慢回调告警（带 `WM_MOUSEMOVE` 等事件类型）证明的是：**UI 线程没被及时调度**——渲染忙、`DispatcherTimer` 轮询（WheelWindow 的 16ms 鼠标轮询）、布局、设置面板打开、字体/配置操作，都会推迟钩子回调的执行。
2. **Windows 的硬上限**：`LowLevelHooksTimeout`（默认 300ms）。回调超时后，Win8+ 会**静默绕过**钩子（键直接漏过去），Win7 会直接把钩子摘掉。代码注释里自己也写着（InputHookService.cs:102-107，`SlowHookWarnMs=20` 是留的余量）。
3. **后果是三类，严重度递增**：
   - **漏吞**：Win 键按下那一刻 UI 线程正忙 → 回调晚了 → `swallow` 没生效 → 开始菜单弹出来，用户可见的 bug；
   - **状态机卡死**：错过一次 `VK_LWIN` 的 keyup → `_armed` 停在 `Win`，后续手势全部失灵；
   - **拖慢全世界**：LL 钩子回调阻塞的是整个系统的输入链——UI 线程忙 1 秒，用户在**所有应用**里打字/动鼠标都延迟 1 秒。
4. GC 停顿对任何线程都生效，所以搬线程**消除不了** GC 暂停；但 UI 线程忙碌是主要矛盾（也是日志实证的那个），搬走后剩余风险只有 GC（通常 gen0/1 < 几 ms）。

---

## 2. 目标架构：专用钩子线程 + 自有消息泵

```
UI 线程（WPF Dispatcher）                    钩子线程（新建，MTA，自持消息泵）
┌─────────────────────────┐                ┌──────────────────────────────────┐
│ MainWindow / WheelWindow │                │ GetMessage / DispatchMessage 循环 │
│ SettingsWindow / 托盘     │                │  ├─ WH_KEYBOARD_LL 回调           │
│                          │   ①事件回调→   │  ├─ WH_MOUSE_LL 回调              │
│  SyncHookSettings ───────┼──快照交换────→ │  ├─ WM_TIMER（SetTimer 替代       │
│  Pause/Resume ───────────┼──线程消息────→ │  │   DispatcherTimer 长按计时）    │
│                          │                │  ├─ 自定义 WM_APP（暂停/恢复/      │
│  OnTrigger/CanTrigger/   │  ←─Dispatcher─ │  │   安装/卸载/补发按键）           │
│  OnRelease/OnCancel ←────┼──BeginInvoke── │  └─ 状态机（全部字段只在本线程）    │
└─────────────────────────┘                └──────────────────────────────────┘
```

**核心不变量（整个重构的宪法）：状态机的每个字段只在钩子线程读写。** 做到这一点，锁、volatile、interlocked 就都和状态机无关；跨线程只传"不可变快照"和"请求消息"。

---

## 3. 需要的具体改动（按文件）

### 3.1 NativeMethods.cs（新增 ~40 行，纯增量）

```csharp
[StructLayout(LayoutKind.Sequential)]
public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }

[DllImport("user32.dll")] public static extern int GetMessageW(out MSG msg, IntPtr hWnd, uint min, uint max);
[DllImport("user32.dll")] public static extern bool PostThreadMessageW(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
[DllImport("user32.dll")] public static extern UIntPtr SetTimer(IntPtr hWnd, uint id, uint timeout, IntPtr timerProc); // hWnd=null → WM_TIMER 进线程队列
[DllImport("user32.dll")] public static extern bool KillTimer(IntPtr hWnd, uint id);
```

P/Invoke 签名均来自 Win32 文档，`SetTimer` 的 `hWnd=null` 重载是官方支持的"线程定时器"用法——`WM_TIMER` 直接由本线程泵分发，**天然保住状态机单线程所有权**（这是用它而不是 `System.Threading.Timer` 的决定性理由：后者回调跑在线程池，状态机就得分段加锁）。

### 3.2 InputHookService.cs（主战场，~730 行里估计动 200 行）

#### A. 钩子线程骨架

- 新增 `_hookThread`、`_hookThreadId`、ready 信号（`ManualResetEventSlim`）。
- 线程入口：
  1. 先 `PeekMessage` 一次**强制创建线程消息队列**——`PostThreadMessage` 到一个还没有队列的线程会失败，这是官方文档明示的竞态，必须挡；
  2. 置 ready 事件；
  3. 进入 `GetMessageW` 循环。
- 线程**保持默认 MTA**（LL 钩子不涉 COM；设 STA 反而引入 COM 重入复杂性），`IsBackground = false` + 退出时显式 `PostThreadMessageW(WM_QUIT)` + `Join(timeout)`，避免进程退出时钩子线程被硬杀。
- 可选：`Thread.Priority = AboveNormal`，让钩子线程在 CPU 争抢时优先被调度（钩子线程 99% 时间阻塞在 `GetMessage`，提权成本≈0）。建议做，一行。

#### B. Start()/Stop()/Dispose()

- `SetWindowsHookExW` / `UnhookWindowsHookEx` **必须搬到钩子线程执行**（钩子绑定安装线程；在 UI 线程装、指望别的线程泵消息是不成立的）。
- `Start()` 变成"投递一条 `WM_APP+INSTALL` 到钩子线程"，可选 `WaitOne` 等待安装完成（`ApplyConfig` 目前同步判断 `IsRunning`，保持语义最省事的做法是安装消息带应答）。
- `Stop()` 同理投递 `WM_APP+UNHOOK`；`Dispose()` 投 WM_QUIT + Join。
- `IsRunning` 跨线程读 → `_kbHook/_mouseHook` 两个字段改由钩子线程独占写、UI 侧用安装应答结果维护一个 `volatile bool`。

#### C. DispatcherTimer → SetTimer

- `Arm()`（:581-590）里 `_holdTimer.Stop/Start/Interval` 换成 `KillTimer(null, 1)` / `SetTimer(null, 1, threshold, 0)`。
- 泵循环里 `message == WM_TIMER && wParam == 1` 时调 `OnHoldElapsed()`。
- 语义等价性：`WM_TIMER` 本来就是低优先级消息（队列空了才分发），与现在 `DispatcherPriority.Input` 的"尽量早但不抢输入"一致；220ms 级别的长按判定没有任何可感知差异。
- `CancelGesture/ResetGestureState/FinishGesture` 里的 `_holdTimer.Stop()` 全部换 `KillTimer`。

#### D. 设置同步：8 个属性 → 不可变快照

现状 `SyncHookSettings()`（MainWindow.xaml.cs:169-183）从 UI 线程直写 `ListenCtrl` 等 8 个属性 + `HoldThresholdMs`，钩子回调线程读——直接搬过去就是数据竞争。

**推荐方案：不可变快照 + 引用交换。**

```csharp
private sealed record HookSettings(bool ListenCtrl, bool ListenWin, bool ListenMiddle,
    bool SuppressWin, bool SuppressMiddle, bool CancelOnOtherKey, bool CancelOnLeftClick,
    int HoldThresholdMs, bool Paused);
private volatile HookSettings _settings = new(...默认值...);
```

- UI 侧 `SyncHookSettings` 构造整份快照后一次 `Interlocked.Exchange`（或直接 volatile 写引用）；
- 钩子回调读 `_settings`，一个引用读，无撕裂、无锁；
- 代价是"配置应用"到"下一次回调看到"之间有一拍的陈旧——对开关类配置完全无害（现状本来也是异步生效的）。

#### E. Pause()/Resume()：置位在 UI，状态清理必须在钩子线程

- `Pause()` 现在做两件事：置 `_paused` + `ResetGestureState()`（:240-244）。后者清的全是钩子线程独占字段——**必须搬到钩子线程执行**。
- 做法：`Pause()` = 快照里置 `Paused=true` + `PostThreadMessageW(WM_APP+RESET)`；钩子线程收到消息后跑 `ResetGestureState()`。
- 顺序坑：如果 RESET 消息比"置 Paused"更早被处理（不可能——先改快照再发消息，钩子线程处理消息时回调读到的已是新快照；单线程内消息处理晚于任何并发回调的返回，但回调可能正在执行中——所以 `ResetGestureState` 执行时可能有并发回调已越过 `if (_paused)` 检查。**现状也有同样的窗口**（`Pause` 置位与 UI 线程上正在执行的回调天然同线程所以没有；搬线程后窗口变宽）。兜底：`WM_APP+RESET` 处理器先读快照确认 `Paused` 仍为真再清状态，且 `KeyboardProc/MouseProc` 的 `_paused` 早退检查放在**函数最顶端**（现在就在很前面，:275/:350，保持即可）。

#### F. 事件回调方向反转（`CanTrigger/OnTrigger/OnRelease/OnCancel`）

这四个事件目前天然在 UI 线程（因为回调就在 UI 线程），订阅者是 MainWindow 的 UI 逻辑（`_detector.IsAllowed`、`ShowWheel`、`HideWheelInternal`、`RunItem`）。搬线程后它们会**在钩子线程被调**——必须反向马歇尔：

- 保留现在的 `Post(Action)`（UI `Dispatcher.BeginInvoke`），方向从"钩子回调里的重活推给 UI"变成"钩子线程的完成事件推给 UI"——**同一个方法，用途反转**，`HasShutdownStarted` 守卫照样有效。
- 具体接线：
  - `FinishGesture`（:662-690）：状态清理留钩子线程，`Post(OnRelease)` 不变；
  - `CancelGesture`（:693-715）：`Post(OnCancel)` 不变；
  - `OnHoldElapsed`（:596-651）**要拆**：
    - 留钩子线程：`_armed/_fired/_cancelled` 检查、中键补发 `mouse_event`（:624-638，Win32 调用无亲和性，且现在就在"泵上下文"跑，语义不变）、置 `_fired = true`；
    - 推给 UI：`CanTrigger` 判定 + `OnTrigger` 唤出轮盘。

#### G. ★ 新引入的竞态：CanTrigger 异步化（整个方案里唯一真正的新问题）

现状：`OnHoldElapsed` 同步问 `CanTrigger()`，答案是"这一刻"的；不允许就 `_cancelled = true` 收场（:613-618）。
改后：`CanTrigger` 跑在 UI，答案回来时**钩子线程的状态可能已经变了**（用户在判定期间松开了触发键）。必须显式处理，三选一：

1. **推荐：判定结果回投钩子线程裁决**。UI 线程算出 `allowed` 后 `PostThreadMessageW(WM_APP+TRIGGER_RESOLVED)`，钩子线程处理器里**重新检查** `_armed == 原来的 kind && !_cancelled && !_fired`，仍然成立才置 `_fired=true` 并 `Post(OnTrigger)`。时序窗口自然闭合，成本一次线程消息（<0.1ms）。
2. 把环境判定整套搬钩子线程（`GetForegroundWindow/GetWindowThreadProcessId` 无线程亲和性）——延迟最低，但 `WindowDetectorService` 有自己的 DispatcherTimer 缓存，等于连它一起搬，改动面翻倍。**第一轮不做**。
3. 接受竞态、靠兜底路径（`OnTrigger` 唤出后 `OnRelease` 发现 `_wheelShown` 立刻收掉）——会出现"轮盘闪一下"的用户可见缺陷。**不推荐**。

注意顺序：方案 1 里"置 `_fired=true`"要发生在裁决消息处理器里，而不是 `OnHoldElapsed` 当场——否则窗口期内 `FinishGesture` 会把 `_fired` 状态和轮盘显示状态打不一致（真机最容易在这里回归，验证清单里单列）。

#### H. 补发按键（Win/中键的吞与补）

铁律**原样保留**：绝不在钩子回调内同步 `SendInput`/`mouse_event`（合成输入会同步穿过本钩子 → 重入）。

改动点只有"补发动作排队到哪"：

- 现状 `Post(() => KeySequence.KeyDown(...))`（:449, :472-476, :577）排到 **UI 线程**——UI 一忙，Win 轻点补发就延迟（开始菜单晚弹）；
- 改为排到**钩子线程自己的泵**：`WM_APP+REPLAY` 类消息（或统一一个"待执行动作队列" + `PostThreadMessage` 通知），泵空闲即执行；
- 好处：补发延迟与 UI 负载彻底解耦——这正是日志里慢回调想治的那个"UI 忙"。
- `OnHoldElapsed` 里的中键补发 `mouse_event`（:630）现在就在泵上下文（DispatcherTimer tick），搬到钩子线程后仍是泵上下文（WM_TIMER），**语义不变，不用动**。

#### I. ReportSlowHook 简化

`Logger` 上一轮已改成后台批量落盘（入队零 I/O），所以 :166 的 `Post(() => Logger.Warn(...))` 可以直接换成钩子线程内 `Logger.Warn(...)`——少一跳 UI，且告警时间戳更接近真实发生时刻。顺带 `_slowHookReports`/`MaxHookMs` 变成钩子线程独占字段。

### 3.3 MainWindow.xaml.cs（~20 行）

- `SyncHookSettings`：8 行属性赋值 → 构造快照 + 一次交换；
- `ApplyConfig`：`Start()/Stop()` 调用点不变（内部已异步化，带应答的话语义不变）；
- `OpenSettingsWindow` 的 `Pause()/Resume()` 调用不变；
- 事件订阅（`CanTrigger/OnTrigger/OnRelease/OnCancel`）不变——回调仍被马歇尔到 UI 线程。

### 3.4 不需要动的（明确划出边界）

- **WheelWindow 的 16ms 鼠标轮询**（点击执行模式）：本来就是独立的 DispatcherTimer，留在 UI；
- `KeySequence` / `ActionExecutor` / `SendInput` P/Invoke：无线程亲和性；
- `WindowDetectorService`：第一轮留在 UI（见 3.2-G-2）；
- 所有 UI/设置/本地化代码。

---

## 4. 风险清单（按血量排序）与真机验证计划

| 风险 | 场景 | 缓解 |
|---|---|---|
| Win 键吞/补发回归 | 轻点 Win（补按下+松开）、Win+E / Win+D（组合键时提前补 KeyDown）、长按唤出后松开、`_winSwallowed/_winReplayed` 残留 | 状态机逻辑**一行不改**，只换执行线程；验证清单逐条过 |
| CanTrigger 异步竞态 | 判定期间松开触发键 | §3.2-G-1 回投裁决；验证时专门做"按住立即松开"的快速手势 |
| 中键接管 | 浏览器中键滚动（拖动>40px 取消）、220ms 接管补发松开、连续两次中键 | 逻辑不变；重点验证接管后立刻松开不吞错 |
| 安装时序 | UI 在钩子线程 ready 前调 Start | ready 事件 + 安装应答 |
| 退出时序 | WM_QUIT 后钩子还挂了一瞬 | unhook 先于 quit 投递、Join 超时兜底 |
| 睡眠/唤醒、锁屏、UAC 安全桌面 | 现状同样跨不过安全桌面，无回归 | 冒烟即可 |

**真机验证清单**（按依赖顺序，全部通过才算完成）：
1. Ctrl 长按唤出 / 松开执行 / Esc 取消 / 其他键取消；
2. Win 长按唤出（吞按下）、轻点开始菜单（补发）、Win+E（提前补 KeyDown 后松开放行）；
3. 中键长按接管（浏览器不卡滚动模式）、中键拖动取消、快速双击中键；
4. 左键取消 vs 点击执行两种模式；
5. 设置面板打开期间打字（Pause 生效）+ 关闭后恢复；
6. `ApplyConfig` 保存后开关即时生效（快照交换）；
7. 压力对比：拖动轮盘动画的同时连续打字，观察 `MaxHookMs` 与漏吞；改前改后各跑一次日志对比；
8. 托盘退出干净（无残留钩子 → 用另一个脚本注入检测，或看任务管理器进程消失即可）。

---

## 5. 实施切分（建议 5 个独立可回退的提交）

1. **NativeMethods 增补** MSG/GetMessage/PostThreadMessage/SetTimer P/Invoke —— 纯增量，零风险；
2. **钩子线程骨架**：线程、泵、ready、Start/Stop/Dispose 改投递（状态机仍在 UI?）—— 注意：这一步做不到"状态机仍在 UI"，因为回调随安装线程走了。所以第 2 步必须同时带上 D（设置快照）与 C（SetTimer），**2+3+4 实际是一次原子重构**，只宜一次提交（可回退点靠 git，不靠中间态编译）。修正后的切分：
   - ① NativeMethods 增补；
   - ② InputHookService 原子重构（线程 + 泵 + 快照 + SetTimer + 事件反马歇尔 + 回投裁决 + 补发改钩子线程排队）；
   - ③ MainWindow 接线适配（快照构造、Start/Stop 调用点）；
   - ④ 日志增强：启动横幅记录"钩子线程已就绪(tid=…)"，慢回调告警从 UI Post 改直写；
   - ⑤ 真机验证 + 观察一周日志再考虑 E-2（环境判定搬线程）等进阶项。
- **可选前置 ⓪**：把状态机抽成纯逻辑类（输入=按键/鼠标事件流，输出=吞/放行+动作请求序列），配单元测试（当前 InputHookService 零测试覆盖，explore 反复确认 "no tests found"）。做这件事能把 ② 的回归风险从"全靠真机"降为"逻辑有网、线程靠真机"。**强烈建议做**，Win/中键那几条注释里血泪史（双份 KeyUp、浏览器卡滚动）说明这状态机的边界行为才是真正的雷区。

## 6. 替代方案及否决理由

| 方案 | 否决理由 |
|---|---|
| 调大注册表 `LowLevelHooksTimeout` | 治标；改的是用户整机全局设置，且上限再大也只是"更晚死" |
| 继续把回调内重活 Post 出去（现状） | 解决的是"回调内耗时"，解决不了"回调根本没被调度"——泵忙则全输 |
| Raw Input（WM_INPUT） | 只观测不拦截；本程序的核心需求恰恰是"吞" Win/中键，不适用 |
| `System.Threading.Timer` 替代长按计时 | 回调在线程池，状态机所有权破碎，需要锁——比 SetTimer 方案差且更险 |
| 全程序上 `async`/消息线程池 | LL 钩子回调线程模型是 Windows 硬性约束，托管线程池不满足"专用泵"要求 |

## 7. 工作量与结论

- 改动集中：`InputHookService.cs`（~200 行净动，730 行文件）+ `NativeMethods.cs`（+~40）+ `MainWindow.xaml.cs`（~20）+ 可选状态机抽离与测试（+~300 测试）。
- 无外部依赖、无新包；唯一不可替代的验证手段是真机（§4 清单）。
- 收益排序：全局输入不再被 UI 负载绑架（治本）→ 漏吞/状态卡死两类用户可见 bug 消失 → 补发延迟与 UI 解耦 → 日志里的慢回调告警应降到 GC 级噪声。
