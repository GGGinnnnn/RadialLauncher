# RadialLauncher 代码审查报告

审查范围：`X:/study/C#/RadialLauncher`（约 11,571 行，单项目 + 新增测试项目）
审查日期：2026-10-13
审查方式：通读全部源码 + 为可测部分编写 300 个单元测试并运行验证

> **状态更新（2026-10-13，修复轮）**：本报告第二节的 3 个 P1 问题**已全部修复**，测试从 300 增至 **339 个，全部通过**；Release 构建 0 警告 0 错误；`--selftest` 自检全部通过。各条目末尾附有「✅ 已修复」说明，原始分析保留以便追溯。

---

## 一、总体评价

整体质量高于同类个人项目：分层清晰（`Models` / `Services` / `Views` / `Controls`）、Win32 互操作集中声明、钩子回调严格遵循「只改状态 + `BeginInvoke` 投递」的 300ms 约束、配置采用「临时文件 + `File.Replace`」原子写入、v1→v2 迁移有明确判据、异常兜底到位、日志失败不影响主流程。日常使用中长时间挂后台的稳定性设计（单实例 + 卡死检测 + `--force` 逃生口）考虑得很周全。

**结论：无阻断性缺陷。** 发现 3 个应当修复的问题（P1）、若干健壮性/可维护性改进点（P2），以及一批可测试性缺口（P3）。

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

### 6. `WheelWindow` 与 `WheelPreview` 的几何计算大面积重复

`LoadWheel`（`WheelWindow.xaml.cs:218-360`）与 `Render`（`WheelPreview.xaml.cs:40-120`）各自实现了一遍：半径、底盘半径、光晕留白、窗口边长、图标尺寸、字号缩放。除上面第 1 条已发现的不一致外，这些魔数（`0.72` / `0.44` / `0.34` / `0.128` / `0.94` / `0.6`）在两处各写一份，后续调整必然再次漂移。

**建议**：抽出 `WheelGeometry`（纯计算，无 WPF 依赖），输入 `AppearanceSettings` + 方向数，输出 `(radius, plateRadius, windowSize, itemSize, iconSize, fontSize)`。这样既消除不一致，又能**直接单元测试**，不用起窗口。

### 7. `KeyStateHelper.WaitForModifiersReleased` 是忙等自旋

**位置**：`Services/KeyStateHelper.cs`

用 `Thread.Sleep(0)` / `Thread.Sleep(1)` 自旋最多 250ms。若在 UI 线程调用会短暂卡界面。实际调用点在 `MainWindow`（有独立 `DispatcherTimer` 版本兜底），但该方法本身容易误用。

**建议**：加注释警示"不要在 UI 线程调用"，或改为 `SpinWait` / 明确标注调用约束。

### 8. `WindowDetectorService.FindByProcess` 未防 `ProcessNames` 为 null

**位置**：`Services/ProfileService.cs:72-83`

`Resolve` 路径上有 `Normalize` 保证 `ProcessNames` 非 null，但 `FindByProcess` 被设置面板直接调用，若传入未经 `Normalize` 的配置（例如刚反序列化的对象或手工构造的对象）会 NRE。

**建议**：循环前加 `if (profile?.ProcessNames == null) continue;`。

### 9. `IconService` 缓存无上限

**位置**：`Services/IconService.cs`

`Cache` 是 `Dictionary`，键含完整路径。若用户在设置里反复切换大量不同的图标路径，缓存会无限增长（每个条目还持有已 `Freeze` 的 `BitmapSource`，占显存）。实践中影响有限，但设置面板是长驻窗口。

**建议**：改成带上限的 LRU，或提供按 key 清理。

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

**结果：339 个测试全部通过**（约 2s；修复 P1 前为 300 个）。

| 测试文件 | 覆盖对象 | 用例数（约） |
|---|---|---|
| `BrushHelperTests` | 颜色解析/格式化/明暗判定 | 30 |
| `KeySequenceTests` | 快捷键文本 → 虚拟键序列解析、描述、往返 | 40 |
| `ConfigServiceNormalizeTests` | 所有 clamp 边界、触发键兜底、语言兜底、幂等性 | 45 |
| `ConfigServiceRoundTripTests` | 落盘往返、深拷贝、损坏 JSON 容错、v1→v2 迁移 | 15 |
| `AppProfileTests` | 进程名/标题关键字匹配 | 18 |
| `ProfileServiceTests` | 全局 vs 专属轮盘路由、方向数覆盖、空项回退 | 25 |
| `ShortcutCatalogTests` | 规范化、查找、搜索、数据完整性（含"全部按键可解析"） | 30 |
| `FontCatalogTests` | 字体回退链拼接、去重、推荐列表 | 22 |
| `LocTests` | 索引器、语言切换、`PropertyChanged`、键命名规范、**中英文表完备性** | 20 |
| `RadialMenuTests` | 角度→索引映射（4 向/8 向）、死区、越界钳制 | 22 |
| `CatalogAndItemTests` | `WheelItem` 克隆、`BuiltinCatalog`（含 `Keys` 表完整性）、`IconService.GuessExecutable` | 30 |
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
| P2 | #4 `FontCatalog` 缓存加锁 | 小 | 待处理 |
| P2 | #6 几何计算统一（与 #1 一并做） | 中 | ✅ 已随 #1 完成 |
| P2 | #8 `FindByProcess` null 防护 | 极小 | 待处理 |
| P2 | #5 删除冗余赋值 | 极小 | 待处理 |
| P2 | #7、#9 注释/缓存上限 | 小 | 待处理 |
| P3 | #10 数据目录可注入（解锁日志轮转测试） | 中 | 待处理 |
| P3 | #12 钩子状态机抽纯函数（收益最大） | 大 | 待处理 |
