# RadialLauncher 代码审查报告

审查范围：`X:/study/C#/RadialLauncher`（约 11,571 行，单项目 + 新增测试项目）
审查日期：2026-10-13
审查方式：通读全部源码 + 为可测部分编写 300 个单元测试并运行验证

> **状态更新（2026-10-13，修复轮）**：本报告第二节的 3 个 P1 问题**已全部修复**，第三节的 5 个 P2 问题**亦已修复**；测试从 300 增至 **355 个，全部通过**；Release 构建 0 警告 0 错误；`--selftest` 自检全部通过。各条目末尾附有「✅ 已修复」说明，原始分析保留以便追溯。

---

## 一、总体评价

整体质量高于同类个人项目：分层清晰（`Models` / `Services` / `Views` / `Controls`）、Win32 互操作集中声明、钩子回调严格遵循「只改状态 + `BeginInvoke` 投递」的 300ms 约束、配置采用「临时文件 + `File.Replace`」原子写入、v1→v2 迁移有明确判据、异常兜底到位、日志失败不影响主流程。日常使用中长时间挂后台的稳定性设计（单实例 + 卡死检测 + `--force` 逃生口）考虑得很周全。

**结论：无阻断性缺陷。** 发现 3 个应当修复的问题（P1）、若干健壮性/可维护性改进点（P2），以及一批可测试性缺口（P3）。

> **修复进展**：P1（3 项）与 P2（5 项）**已全部修复并验证**；P3（2 项）仍待处理 —— 它们不是缺陷，而是"阻碍自动化验证"的设计改造，收益最大的是 #12 钩子状态机抽纯函数。

---

## 二、P1 — 应当修复的问题

### 1. `WheelPreview` 四向轮盘的窗口尺寸算错

**位置**：`Views/WheelPreview.xaml.cs:45`

```csharp
double baseWindow = 2 * (_appearance.ItemSize * 1.95 + _appearance.ItemSize * 0.72 + 8);
```

**问题**：这里把半径系数硬编码成 `1.95`，没有区分 4 向 / 8 向。而 `WheelWindow.LoadWheel()`（`WheelWindow.xaml.cs:223`）是：

```csharp
double radius = directionCount == 4 ? itemSize * 2.05 : itemSize * 1.95;
```

四向模式下真实半径是 `itemSize * 2.05`，但预览按 `1.95` 算窗口大小，导致**四向轮盘在设置面板里的预览会被裁掉一圈**（`fit` 缩放按偏小的基准算，实际内容溢出）。

**建议**：把半径/尺寸计算抽成一个共享方法（例如放在 `RadialMenu` 或新的 `WheelGeometry` 静态类），`WheelWindow` 与 `WheelPreview` 都调用它，避免两处魔数各自漂移。

**✅ 已修复**：新增 `Services/WheelGeometry.cs`（纯计算、无 WPF 依赖的静态类），`WheelWindow.LoadWheel` 与 `WheelPreview.Render` 均改为调用 `WheelGeometry.Compute` / `FitScaleFor`，两处几何魔数（`2.05` / `1.95` / `0.72` / `0.44` / `0.34` / `0.128` / `0.94` / `0.6`）已全部收敛到 `WheelGeometry` 常量。四向预览裁剪 bug 随之消除。

同时把「适配可用区域的缩放」也抽成了 `FitScaleFor`：**扣掉固定光晕外框后再算缩放**（`fit = (usable - 2*glow) / scalableBase`），修正了初版直接用 `usable / windowSize` 时「窗口最小值被固定 `GlowPadding=36` 顶住、无法压到可用区域内」的数学错误。

新增 `RadialLauncher.Tests/WheelGeometryTests.cs`（约 25 个用例），其中 `Compute_FourWayWindowIsLargerThanEightWay` 作为本次裁剪 bug 的回归护栏。

---

### 2. `ConfigService.Normalize` 对 `Appearance` / `Trigger` / `System` 分组缺少 null 兜底

**位置**：`Services/ConfigService.cs:136-170`

```csharp
c.Appearance.ItemCount = c.Appearance.ItemCount == 4 ? 4 : 8;   // ← 若 c.Appearance 为 null 直接 NRE
...
c.System.Language = ...
...
c.Environment ??= new EnvironmentSettings();   // ← 只兜了 Environment
c.WheelItems ??= WheelItem.CreateDefaults();
c.Profiles ??= new();
```

**问题**：`Normalize` 只对 `Environment` / `WheelItems` / `Profiles` 做了 null 兜底。如果 `config.json` 里手写或工具写入了 `"Appearance": null`（`System.Text.Json` 会把它反序列化成 `null`），`Load()` 会在 `Normalize` 里抛 `NullReferenceException`，被外层 `catch` 吞掉后**静默回退到默认配置**——用户会感觉"我的配置莫名其妙全没了"，而日志里只有一条笼统的"读取配置失败"。

**已用测试固化当前行为**（`ConfigServiceNormalizeTests.Normalize_DoesNotBackfillNullAppearanceGroup`）：一旦补上兜底，该测试会失败，正好提醒更新。

**建议**：在 `Normalize` 开头补三行：

```csharp
c.Appearance ??= new AppearanceSettings();
c.Trigger    ??= new TriggerSettings();
c.System     ??= new SystemSettings();
```

**✅ 已修复**：三行兜底已加在 `Normalize` 方法**开头**（任何字段访问之前），并在遍历 `c.Profiles` 时补了 `if (profile == null) continue;` 防护。原先用于固化缺陷的 `Normalize_DoesNotBackfillNullAppearanceGroup` 已删除，替换为 `Normalize_BackfillsNullGroupObjects`（Theory 覆盖三个分组）等 5 个新用例。

---

### 3. `ActionExecutor` 媒体键分支把内部 id 当按键文本用（隐式耦合）

**位置**：`Services/ActionExecutor.cs:309-316`

```csharp
case "PlayPause":
    ...
case "VolumeUp":
    ...
    return SendKeysOk(id);   // id 形如 "VolumeUp"
```

**问题**：`SendKeysOk(id)` 会把 `BuiltinCatalog` 的内部 id 字符串直接交给 `KeySequence.TryParse`，**隐式依赖 `KeySequence.Map` 中恰好存在同名键**（`["VolumeUp"]`、`["PlayPause"]` 等）。目前两处名字是对齐的，所以能用；但只要有人改了 `BuiltinCatalog` 的 id（比如改成 `volume-up`）或在 `KeySequence.Map` 里重命名，编译期毫无提示，运行期会静默返回 `Msg_InvalidShortcut`。

**建议**：在 `BuiltinCatalog` 的定义里显式加一列按键序列（例如 `BuiltinDefinition` 增加 `Keys` 字段），或在 `ActionExecutor` 里用显式映射表把 id → 按键序列隔离出来；同时补一条测试：**遍历 `BuiltinCatalog.Items`，凡是被 `RunBuiltin` 当作快捷键处理的一律断言 `KeySequence.TryParse` 成功**。

**✅ 已修复**：`BuiltinDefinition` record 新增可选 `Keys` 字段，全部 14 个「走合成快捷键」的条目（`ShowDesktop` / `OpenExplorer` / 媒体键 / `Clipboard` / `Screenshot` / `EmojiPanel` / `Search` / `RunDialog`）显式填上按键序列；启动进程/调 API 的条目保持 `Keys = null`。新增 `BuiltinCatalog.KeysFor(string id)` 作为唯一查表入口。

`ActionExecutor` 侧删除了原 `case "ShowDesktop"` / `case "OpenExplorer"` 里的硬编码 `SendKeysOk("Win+D"/"Win+E")`，全部统一到合并 case → 新增的 `SendBuiltinKeys(id)`，由它查 `KeysFor` 并给出「未配置」/「无法解析」两种明确失败信息。旧的死代码 `SendKeysOk` 已删除。

新增回归护栏 `AllDefinedKeys_AreParsableByKeySequence`（遍历目录断言全部按键可解析）+ 4 个用例验证 `KeysFor` 的语义。

---

## 三、P2 — 健壮性与可维护性

### 4. `FontCatalog` 两个惰性缓存未加锁

**位置**：`Services/FontCatalog.cs:49-109`

`_installed` 与 `_installedSorted` 是普通静态字段，`InstalledSet` / `InstalledFamilies` 首次访问时会枚举全部系统字体（开销不小）。多个线程同时首次访问会**重复枚举**，并且 `_installedSorted` 的赋值不是原子的，理论上另一个线程可能读到"已赋值但内容尚未完全可见"的列表。

**建议**：用 `Lazy<HashSet<string>>` / `Lazy<List<string>>`，或在 getter 内加 `lock`。注意 `InvalidateCache()` 也要相应改成重建 `Lazy`。

**✅ 已修复**：改为**双检锁 + `Volatile` 发布**。快路径用 `Volatile.Read` 无锁读引用；未命中时进 `lock (CacheGate)` 再查一次；构造完成后用 `Volatile.Write` 发布引用，避免其他线程看到"引用已赋值但内容对其他 core 尚未可见"的半成品。`InvalidateCache()` 也在锁内清两个引用并保证可见性。

新增 4 个测试：同一实例复用、失效后重建、排序与去重、以及 `ConcurrentFirstAccess_DoesNotThrowAndYieldsConsistentCache`（8 线程 `Barrier` 同时首次访问，断言拿到同一实例且无异常）——正是本条描述的并发缺陷的回归护栏。

### 5. `InputHookService.Dispose` 中的冗余赋值

**位置**：`Services/InputHookService.cs:429` 附近

```csharp
if (!_winReplayed)
{
    _winReplayed = false;   // ← 已在 !_winReplayed 分支内，此赋值无意义
    ...
}
```

**建议**：删除此行。属于纯噪声，但会让人误以为这里有状态机技巧。

**✅ 已修复**：该行已删除，并补了一句注释说明这是"拦截期间监听被关掉"的异常路径。

### 6. `WheelWindow` 与 `WheelPreview` 的几何计算大面积重复

`LoadWheel`（`WheelWindow.xaml.cs:218-360`）与 `Render`（`WheelPreview.xaml.cs:40-120`）各自实现了一遍：半径、底盘半径、光晕留白、窗口边长、图标尺寸、字号缩放。除上面第 1 条已发现的不一致外，这些魔数（`0.72` / `0.44` / `0.34` / `0.128` / `0.94` / `0.6`）在两处各写一份，后续调整必然再次漂移。

**建议**：抽出 `WheelGeometry`（纯计算，无 WPF 依赖），输入 `AppearanceSettings` + 方向数，输出 `(radius, plateRadius, windowSize, itemSize, iconSize, fontSize)`。这样既消除不一致，又能**直接单元测试**，不用起窗口。

**✅ 已修复**：已随 P1-1 一并完成 —— `Services/WheelGeometry.cs` 即本条建议的产物。

### 7. `KeyStateHelper.WaitForModifiersReleased` 是忙等自旋

**位置**：`Services/KeyStateHelper.cs`

用 `Thread.Sleep(0)` / `Thread.Sleep(1)` 自旋最多 250ms。若在 UI 线程调用会短暂卡界面。实际调用点在 `MainWindow`（有独立 `DispatcherTimer` 版本兜底），但该方法本身容易误用。

**建议**：加注释警示"不要在 UI 线程调用"，或改为 `SpinWait` / 明确标注调用约束。

**✅ 已修复**：两处都做了。
1. 自旋改为 `SpinWait.SpinOnce()`（会在若干次后自行升级为 yield/Sleep(1)，比手写 `Sleep(0)/Sleep(1)` 更贴合 CPU 情况），并补了 `maxMs < 0` 的入参规整。
2. XML 文档里加了显式的 **「调用约束：不要在 UI 线程直接调用」** 段落，说明最坏会占用调用线程 `maxMs` 毫秒、当前唯一生产调用点在钩子线程、UI 侧应走 `MainWindow` 的 `DispatcherTimer` 异步路径。

> 附带发现：该方法当前**没有任何调用者**（`MainWindow` 用的是自己的 `DispatcherTimer` 异步实现）。本次保留它作为同步 API 备用，但已在文档注释里写清约束。若后续确认不需要同步版本，可整个删除。

### 8. `WindowDetectorService.FindByProcess` 未防 `ProcessNames` 为 null

**位置**：`Services/ProfileService.cs:72-83`

`Resolve` 路径上有 `Normalize` 保证 `ProcessNames` 非 null，但 `FindByProcess` 被设置面板直接调用，若传入未经 `Normalize` 的配置（例如刚反序列化的对象或手工构造的对象）会 NRE。

**建议**：循环前加 `if (profile?.ProcessNames == null) continue;`。

**✅ 已修复**：已加 `if (profile?.ProcessNames == null) continue;`，并在入口补了 `string.IsNullOrEmpty(processName)` 的早退（空目标名不可能匹配到任何配置）。

新增 5 个测试：`ProcessNames` 为 null、`Profiles` 混入 null 元素、全为坏条目、以及 `[Theory]` 覆盖空/`null` 目标名。

### 9. `IconService` 缓存无上限

**位置**：`Services/IconService.cs`

`Cache` 是 `Dictionary`，键含完整路径。若用户在设置里反复切换大量不同的图标路径，缓存会无限增长（每个条目还持有已 `Freeze` 的 `BitmapSource`，占显存）。实践中影响有限，但设置面板是长驻窗口。

**建议**：改成带上限的 LRU，或提供按 key 清理。

**✅ 已修复**：改为 **LRU，上限 256 条**（`Dictionary<string, LinkedListNode<CacheEntry>>` + `LinkedList<CacheEntry>`，查找/插入/淘汰均 O(1)）。命中即把节点提到队尾；超限时从队头淘汰最久未使用者。另提供 `public static void ClearCache()` 供设置面板关闭或用户刷新时主动清理，以及 `public static int CacheCount` 用于诊断/测试。

保留了原有语义：**失败结果（null）同样入缓存**——"这个路径确实没有图标"也是有效结论，否则每次经过设置面板都会重新问一遍 shell32。

新增 7 个测试：清空、负结果缓存、400 次不同路径后不超上限、近期条目仍命中、大/小图标键分离、空路径不入缓存、4 线程并发不抛异常且不超上限。

---

## 四、P3 — 可测试性缺口

审查过程中为写测试而发现的、**阻碍自动化验证**的设计问题：

### 10. `Logger` / `ConfigService` 的数据目录写死

```csharp
// Logger.cs:46
public static string DirectoryPath { get; } = Path.Combine(..., "RadialLauncher");
// ConfigService.cs:21
public static string DataDirectory { get; } = Path.Combine(..., "RadialLauncher");
```

两者都是 **get-only 静态属性**，无法在测试中重定向。后果：
- 无法直接测试"日志轮转（超过 1MB 移到 `.old`）"这类核心逻辑；
- 测试会读写用户**真实的 `%AppData%\RadialLauncher`**，必须靠 fixture 备份/还原（见 `ConfigServiceRoundTripTests`）。

本轮测试的实现方式：`LoggerTests` 只测不离盘的分支（`MinLevel` 过滤）；`ConfigServiceRoundTripTests` 用 `IDisposable` 快照并还原 `config.json` / `config.backup.json`，并清理 `config.corrupt-*` 与 `.tmp`。

**建议**：把目录改为可注入（构造函数参数或可写属性），默认为 `%AppData%\RadialLauncher`。这样日志轮转、原子写入失败回退等路径都能被真正覆盖。

### 11. `ActionExecutor` 的 URL 归一化逻辑不可达

`OpenUrl` 里"无 `://` 且不以 `mailto:` 开头就补 `https://`"这段规则很值得测，但它是 `private` 且与 `Process.Start` 耦合。

**建议**：抽成 `static string NormalizeUrl(string)`,即可直接测试。

### 12. `InputHookService` 状态机难以测试

`_armed` / `_fired` / `_winSwallowed` / `_winReplayed` / `_midPassedThrough` 等状态只通过 `KeyboardProc` / `MouseProc`（依赖真实 `lParam` 指针）驱动。这是全项目**逻辑最复杂、最容易出错**的部分（Win 键补发、中键拖动取消、阈值接管），却完全无法自动化验证。

**建议**：把"收到某个低级输入事件 → 状态迁移 + 应放行/吞下/合成什么"抽成纯粹的决策函数，钩子回调只做"解析 `lParam` → 调决策函数 → 执行副作用"。抽出后可用表驱动测试覆盖 Win+E 补发、中键拖动取消等关键场景。

---

## 五、单元测试交付物

新增项目 `RadialLauncher.Tests`（xUnit，`net8.0-windows` + `UseWPF`，已加入 `RadialLauncher.slnx`）。

**结果：355 个测试全部通过**（约 7s；修复 P1 前为 300 个，P1 修复后 339，P2 修复后 355）。

| 测试文件 | 覆盖对象 | 用例数（约） |
|---|---|---|
| `BrushHelperTests` | 颜色解析/格式化/明暗判定 | 30 |
| `KeySequenceTests` | 快捷键文本 → 虚拟键序列解析、描述、往返 | 40 |
| `ConfigServiceNormalizeTests` | 所有 clamp 边界、触发键兜底、语言兜底、幂等性 | 45 |
| `ConfigServiceRoundTripTests` | 落盘往返、深拷贝、损坏 JSON 容错、v1→v2 迁移 | 15 |
| `AppProfileTests` | 进程名/标题关键字匹配 | 18 |
| `ProfileServiceTests` | 全局 vs 专属轮盘路由、方向数覆盖、空项回退 | 25 |
| `ShortcutCatalogTests` | 规范化、查找、搜索、数据完整性（含"全部按键可解析"） | 30 |
| `FontCatalogTests` | 字体回退链拼接、去重、推荐列表、惰性缓存复用与并发一致性 | 26 |
| `LocTests` | 索引器、语言切换、`PropertyChanged`、键命名规范、**中英文表完备性** | 20 |
| `RadialMenuTests` | 角度→索引映射（4 向/8 向）、死区、越界钳制 | 22 |
| `CatalogAndItemTests` | `WheelItem` 克隆、`BuiltinCatalog`（含 `Keys` 表完整性）、`IconService.GuessExecutable`、**图标缓存 LRU** | 37 |
| `WheelGeometryTests` | 方向数规整、半径系数、光晕留白、缩放组合、`FitScaleFor` 适配 | 25 |
| `LoggerTests` | 级别过滤、路径约束、枚举语义 | 8 |

### 测试基础设施中值得保留的两点

1. **`AssemblyInfo.cs` 关闭并行**：被测代码有进程内单例（`Loc.Instance`）与静态缓存，并行会污染语言状态。
2. **`StaRunner`**：`RadialMenu` 继承 `Panel`，构造需要 STA 线程，而 xUnit 默认 MTA。用临时的 STA 线程跑用例并把异常原样抛回，是测试 WPF 控件的最轻做法。

### 测试直接验证出来的资产

- **`ShortcutCatalog` 全部 228 条快捷键都能被合成器解析**（`AllItems_KeysAreParsable`）——这条断言比人工抽查有效得多，任何新加的错字都会被拦下。
- **中英文表键集合一致**（`AllKeys_HaveEnglishTranslation`）——防止英文模式下静默回退成中文。
- **`WheelItem.CreateDefaults()` 里的快捷键全部可解析**——默认配置开箱即用。
- **迁移后的按键序列全部可解析**——保证 v1 用户的旧配置不会静默失效。

---

## 六、修复优先级建议

| 优先级 | 项 | 工作量 | 状态 |
|---|---|---|---|
| P1 | #2 `Normalize` 补三行 null 兜底 | 极小 | ✅ 已完成 |
| P1 | #1 `WheelGeometry` 抽取，消除四向预览裁剪 | 小 | ✅ 已完成 |
| P1 | #3 媒体键映射改为显式表 + 补测试 | 小 | ✅ 已完成 |
| P2 | #4 `FontCatalog` 缓存加锁 | 小 | ✅ 已完成 |
| P2 | #6 几何计算统一（与 #1 一并做） | 中 | ✅ 已随 #1 完成 |
| P2 | #8 `FindByProcess` null 防护 | 极小 | ✅ 已完成 |
| P2 | #5 删除冗余赋值 | 极小 | ✅ 已完成 |
| P2 | #7、#9 注释/缓存上限 | 小 | ✅ 已完成 |
| P3 | #10 数据目录可注入（解锁日志轮转测试） | 中 | 待处理 |
| P3 | #12 钩子状态机抽纯函数（收益最大） | 大 | 待处理 |

---

## 七、按键监听逻辑专项审查（2026-10-13）

> 对象：`Services/InputHookService.cs`（717 行，`WH_KEYBOARD_LL` / `WH_MOUSE_LL` 全局钩子）
> 及配套的 `KeyStateHelper` / `KeySequence` / `MainWindow` 修饰键等待路径。
> 本轮为**针对该子系统的专项复查**，编号独立于上面第 2–4 节。

### 总体结论

"钩子回调内只改状态、副作用一律 `BeginInvoke` 回消息循环"这条铁律在全文 717 行无一处违反；
"用 `dwExtraInfo` 而非 `LLKHF_INJECTED` 过滤自注入输入"的取舍正确（不会顺带屏蔽
AutoHotkey / 辅助功能软件）。三种触发键的拦截策略（Ctrl 只观察、Win 吞下+回放、
中键先放行+接管）设计意图清晰。但状态迁移存在若干"标志跨手势残留"的缺陷。

### P1（行为缺陷，本轮已修复）

| 编号 | 问题 | 修复 |
|---|---|---|
| P1-A | 中键 `_midReleaseInjected` 跨手势残留：`HandleMiddleDown` 在 `_armed != None` 时早退且不重置标志，导致下一次真实松开被吞，系统以为中键一直按着（浏览器卡在中键滚动模式） | `HandleMiddleDown` 早退分支 + `FinishGesture` 均归零 `_midPassedThrough` / `_midReleaseInjected` |
| P1-B | `ListenWin` 中途关闭的补发路径与 `HandleWinUp` 的兜底**各自实现**同一逻辑，靠 `else` 互斥隐性维持，任何改动都可能变成双份 `KeyUp`；且旧路径从不 `FinishGesture`，会留下 `_armed == Win` 残影 | Win / 中键的补发出口分别收敛为唯一的 `HandleWinUp()` / `HandleMiddleUp()` |
| P1-C | 修饰键等待超时后仅记 `Warn` 就**照常执行**，`Win+V` 仍会被合成成 `Ctrl+Win+V`（轻则无响应、重则触发系统提示音） | 新增 `KeyStateHelper.ForceReleaseAllDown()`：超时后先强制补发松开，复核 `AnyModifierDown()`；仍按着则**放弃执行**并弹 Toast（新增 `Msg_ActionBlockedModifier` / `Msg_ActionBlockedModifierHint`） |

> ⚠️ **修复 P1-A 时踩到的坑**：`HandleMiddleUp` 原本在 `FinishGesture()` **之后**读
> `_midReleaseInjected`；给 `FinishGesture` 加了清零后，这里会永远读到 `false`，
> 恰好会破坏"接管后吞掉真实松开"这一核心行为。必须把 `bool injected = _midReleaseInjected;`
> **移到 `FinishGesture()` 之前**。`FinishGesture` 的 XML 注释已写明该副作用顺序约束。

### P2（健壮性 / 可维护性，本轮已全部修复）

| 编号 | 问题 | 修复 |
|---|---|---|
| P2-A | `SettingsWindow.IsRecordingKey` 硬编码 `false`，`MainWindow.CanTrigger` 的守卫是**死代码**；`IAppHost.PauseHook` / `ResumeHook` 是**零调用的死接口成员** | 三处死成员全部删除；`CanTrigger` 改为注释说明"设置面板打开期间由 `_hook.Pause()` 整段挂起" |
| P2-B | `KeyStateHelper.WaitForModifiersReleased` 全项目**无调用方**，且 XML 注释（"唯一生产调用点在钩子回调里"）已过期 | 删除该方法，连带清掉不再需要的 `using System;` / `using System.Threading;` |
| P2-C | `ScreenColorPickerWindow` 的 Esc 用 `IsDown` **电平**检测，而左右键用 `_leftWasDown` / `_rightWasDown` **边沿**检测，模式不一致；按住 Esc 会每 20ms 反复触发取消 | 新增 `_escWasDown`，Esc 改为边沿检测，与左右键统一 |
| P2-D | `Dispose` 的 `midStuck = _midPassedThrough && !_midReleaseInjected` 未加 `SuppressMiddle` 条件：关闭中键抑制时该式恒为 `true`，进程退出会平白注入一次 MIDDLEUP | **✅ 上一轮已修**：加 `SuppressMiddle &&`，与 `OnHoldElapsed` 的接管条件对齐 |

> ⚠️ **对上一轮判断的更正**：上一轮曾断言"录制按键的 Picker 窗口打开时钩子并未 `Pause`"，
> 这是**错误的**。追查调用链后确认：所有 Picker 只从 `SettingsWindow` 打开，而
> `MainWindow.OpenSettingsWindow` **开面板就调 `_hook.Pause()`**（关闭时 `Resume()`），
> `--settings` 启动参数也走同一路径 —— 所以**录制期间钩子确实被暂停了**，
> 只是走的是具体类型 `_hook.Pause()` 而非那个死接口方法。
> 教训：**"caller 数量为零"只能证明接口成员是死代码，不能证明该能力缺失** ——
> 必须把调用链查完才能下结论。原错误判断已在此更正。

### P3（结构性，建议单开一轮）

第 12 条（钩子状态机抽纯函数）在本轮复查后**优先级应再提高**：P1-A / P1-B / P2-D
三个问题全部是"某个标志该不该在某个分支清零"这一类，正是"状态与决策耦合"的必然产物。
现有 355 个测试中没有一个覆盖钩子状态机（它依赖真实输入，无法在进程内驱动）。
建议先抽 `record struct HookState` + 纯函数 `Decide(state, event, options) -> (nextState, swallow, effects)`，
把 6 个 `Handle*` 方法改为"状态输入 → 决策输出"，副作用由回调层执行 —— 改动集中在一个文件，
风险可控，收益是让这类缺陷从"靠人肉时序推理"变为"靠表驱动测试证明"。

> **代码审查补充交付**：专项审查结论 + P1×3 修复说明 + P2 清单，见本记录；P2-1/P2-2/P2-3 仍未处理。

### 用 codegraph 交叉验证（2026-10-13，工具复核）

本项目已建立 codegraph 索引（`codegraph init`，50 文件 / 1,595 节点 / 3,728 边）。
用它的**调用路径 + 动态派发 + 爆炸半径**三个维度复核上面的结论，得到两条工具级硬证据：

1. **`PauseHook` / `ResumeHook` 是死接口成员**（P2-A 的一半）。
   codegraph 的动态派发合成显示这两个方法**各只有 1 个 caller，就是 `IAppHost` 接口声明本身**；
   `MainWindow.OpenSettingsWindow` 是直接调 `_hook.Pause()`（具体类型），并未走接口。
   全仓 grep 复核：`PauseHook` / `ResumeHook` 仅出现在 `IAppHost.cs`（声明）与
   `MainWindow.xaml.cs`（实现），**调用点为零**。
   而 `IAppHost.PauseHook` 的注释却写着"暂停全局钩子（**例如在设置里录制按键时**）"——
   承诺了一个没人用的能力。→ 已删除这两个接口成员及其实现。

2. **钩子子系统零测试覆盖**（证实 P3 的核心论据）。
   codegraph 对 `KeyboardProc` / `MouseProc` / `HandleWinUp` / `HandleMiddleUp` /
   `HandleMiddleDown` / `OnHoldElapsed` / `HandleOtherKeyDown` / `CancelGesture` 全部报
   "no tests found within 3 caller hops"；grep 测试项目确认
   `InputHookService` / `TriggerKind` / `KeyStateHelper` / `ForceReleaseAllDown` **均无任何引用**。
   即：本轮 P1×3 的修复**没有任何自动化测试守护**，只能靠人工时序推理。

> **结论**：codegraph 把 P2-A 与 P3 从"读代码得出的判断"升级为"工具可复现的事实"。
> 但它也提醒了一个边界：**"caller 数量为零"只说明接口成员是死代码，
> 不等于该能力缺失** —— 能力可能由等价实现（这里的 `_hook.Pause()`）提供。
> `.codegraph/` 体积约 4.6 MB，自带 `.gitignore`，不会污染仓库。
