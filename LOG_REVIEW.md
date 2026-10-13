# 日志反查代码评审（RadialLauncher）

- 输入：`C:\Users\zmal\AppData\Roaming\RadialLauncher\RadialLauncher.log`（1027 行，2026-10-13 17:29 ~ 20:04）
- 关联：`RadialLauncher.log` / `selftest.log` / `config.json` / `config.backup.json`
- 日期：2026-10-13（承接 CODE_REVIEW.md 的修复轮，本篇只写“从日志能反推出来”的问题）

---

## 0. 结论先行

**日志里绝大部分“异常”不是产品缺陷，而是单元测试跑进了你的真实运行环境。**

日志文件是**程序运行日志和 `dotnet test` 输出混在同一个文件里**：`Logger.LogPath` 与
`ConfigService.DataDirectory` 都硬编码到 `%AppData%\RadialLauncher`，测试项目引用同一个程序集，
于是测试既往你的日志里写、又读写你的真实 `config.json`。

除此之外，日志暴露了 **3 个真实代码问题**（迁移失败丢配置、迁移日志版本号恒错、损坏时不使用备份）
和 **2 个架构级隐患**（钩子回调线程、日志同步 I/O）。

---

## 1. 日志噪音的逐条归因

| 日志现象 | 真实来源 | 证据 |
|---|---|---|
| `[ERROR] 读取配置失败，回退到默认配置` + `System.Text.Json.JsonException: 't' is an invalid start of a property name ... BytePositionInLine: 2` + `损坏的配置已备份到 config.corrupt-*.json` | `RadialLauncher.Tests/ConfigServiceRoundTripTests.cs:191` `Load_CorruptJsonFallsBackToDefaults`，它写入 `"{ this is not valid json "` | 该字符串 offset=0 是 `{`、offset=1 是空格、**offset=2 正是 `t`**，与异常里的 `BytePositionInLine: 2` 逐字节吻合。当前目录里没有 `config.corrupt-*`，正是因为测试的 `Dispose()` 把它们删掉了 |
| `[WARN] 应用专属配置「空配置」没有任何功能项，暂时使用全局轮盘` | `RadialLauncher.Tests/ProfileServiceTests.cs:158` 中 `Name = "空配置"` 的用例 | 你真实的 config.json 里只有「浏览器 / VS Code / 资源管理器」三个 profile，每个都有 8 项，**不存在**名为「空配置」的 profile |
| `[INFO] 界面语言切换为 en` / `zh-CN` 成对刷屏（单次 20+ 行） | `RadialLauncher.Tests/LocTests.cs`：构造函数 `SetLanguage("zh-CN")`、`Dispose()` 复位，`[Theory]` 依次注入 `en / en-US / EN / En-GB` | 与你的配置 `Language = zh-CN` 且从不变化互相矛盾；`en → zh-CN → En-GB → zh-CN → en-US …` 的成对模式正是“每个用例前复位、用例中切换” |
| `[INFO] 字体缓存已清空，下次读取将重新枚举系统字体` ×4 | `RadialLauncher.Tests/FontCatalogTests.cs:182/195/206/251` → `FontCatalog.InvalidateCache()`（FontCatalog.cs:152 打这条日志） | 全项目只有测试和设置面板“刷新字体列表”会调 `InvalidateCache` |
| `未找到配置文件，已创建默认配置` / `配置已恢复默认` / `检测到旧版配置（v2）` ×4 / `配置已加载：Ctrl=True Win=True 中键=False` | `ConfigServiceRoundTripTests` 的 `Load_MissingFileCreatesDefaultConfig` / `ResetToDefaults_RestoresDefaultValues` / `Load_MigratesLegacyFlatConfig` 等 | 这些序列**每次测试运行出现一次**，且与同一时间窗口内的「启动/退出」横幅无关 |

有意思的是，测试的日志时间戳紧贴程序运行窗口（如 18:28:28 程序启动 → 18:36:03 测试序列），
说明你是**一边挂着程序，一边在 IDE 里跑测试**——这正是下面第 2 条风险的来源。

## 2. 由噪音暴露的真实风险（P1）

测试直接操作生产环境的 `config.json`，带来四个不只是“噪音”的后果：

1. **配置真会丢**：`ConfigServiceRoundTripTests` 靠 `Dispose()` 快照还原。一旦测试中途抛异常、被
   Stop 或 Ctrl+C，`Dispose()` 不执行，你的 `config.json` 会停在测试内容上（某些用例甚至直接
   `File.Delete(ConfigService.ConfigPath)`）。
2. **取证文件被顺手删掉**：`Dispose()` 会清理 `config.corrupt-*.json`——也就是真正配置损坏时的
   备份证据，被测试一起删了（日志里出现过 8 次“损坏的配置已备份到 …”，现在一个都不剩）。
3. **并发写同一文件**：18:36:03 测试在写 `config.json` 时，18:28:28 启动的那个程序实例还在跑
   （`File.Replace` vs 测试的 `File.WriteAllText`），存在真实的互相覆盖竞态。
4. **排障被误导**：本次任务本身就是例子。

**建议（按性价比排序）**

- 给 `ConfigService.DataDirectory`（ConfigService.cs:21-25）和 `Logger.DirectoryPath`（Logger.cs:46-50）
  改成**可重定向**：静态可写属性 → 环境变量 `RADIALLAUNCHER_DATA_DIR` → 默认 `%AppData%\RadialLauncher`。
  测试 fixture 在构造时指到 `Path.GetTempPath()`，`Dispose` 时删临时目录。这是唯一治本的做法。
- 兜底：测试开头 `Logger.MinLevel = LogLevel.Error;`（配合 `[assembly: CollectionBehavior]` 减少噪音），
  但**不能替代**目录重定向。
- 顺手把 `Dispose()` 的清理范围从 `config.corrupt-*.json` 收窄成“本次测试自己产生的文件名”。

---

## 3. 日志暴露的真实代码问题

### B1（P1）迁移失败会静默清空用户配置 —— `ConfigService.cs:248-287`

```csharp
private static AppConfig Migrate(string json, AppConfig alreadyParsed)
{
    var result = new AppConfig();            // ← 全默认值的空配置
    try { ... }
    catch (Exception ex)
    {
        Logger.Error("迁移旧配置失败，改用默认配置", ex);
        return result;                       // ★ 用户配置被整体丢弃
    }
    return result;
}
```

`Load()` 拿到返回值后会立刻 `Save()`（ConfigService.cs:60-63）把这份“默认配置”写回磁盘，
**用户的配色、功能项、应用专属配置全部不可恢复**。

同一方法第 255 行却是 `if (legacy == null) return alreadyParsed;`——保留了已解析内容。
两处策略不一致，说明 catch 分支是漏改。

**建议**：catch 里 `return alreadyParsed;`；更好的做法是把 `result` 基于 `alreadyParsed` 构建
（先深拷贝已解析结果，再用 legacy 字段逐项覆盖），失败时至少不丢数据。

### B2（P1）迁移日志的版本号永远是错的 —— `ConfigService.cs:57-59`

```csharp
if (LooksLikeLegacy(json) || loaded.SchemaVersion < 2)
    Logger.Info($"检测到旧版配置（v{loaded.SchemaVersion}），开始迁移");
```

`loaded` 是把**旧版扁平文件**反序列化成 `AppConfig` 得到的对象，`SchemaVersion` 取的是属性默认值
**2**（AppConfig.cs:11），而 v1 文件里根本没有这个键——所以日志永远打印 “v2”。日志里 17:34:59、
17:36:22、18:36:03… 那几次“检测到旧版配置（v2）”实际都是 v1，排障时会把方向带偏。

**建议**：改为 `"检测到旧版扁平配置（缺少 Trigger/Appearance 分组），开始迁移"`；
若确实想要版本号，就用 `JsonDocument` 读根节点的 `SchemaVersion`，读不到写 `v1(隐式)`。

### B3（P2）有 `config.backup.json` 却从不使用 —— `ConfigService.cs:73-76 / 87-94`

`Save()` 用 `File.Replace(tmp, ConfigPath, BackupPath, true)`，**每次保存都会留下一份完好的上一版配置**；
但 `Load()` 的容错分支只做“备份坏文件 + 重置默认”，从不回头看 backup：

```csharp
catch (Exception ex)
{
    Logger.Error("读取配置失败，回退到默认配置", ex);
    TryBackupCorruptFile();
    Config = new AppConfig();       // ← backup 就在这里躺着，却不用
}
```

**建议**：catch 里先读 `BackupPath`，能解析成功→ `Config = backup` + `Save()` +
`Logger.Warn("已从 config.backup.json 恢复")`；两条路都失败再回默认。用户体感差别很大。

### B4（P2）钩子回调 20~40ms 的告警：**代码不慢，是线程被抢占**

日志：`18:31:05` 鼠标 23.1 / 37.3 ms，`18:31:09` 25.7 ms。

看 `MouseProc`（InputHookService.cs:310-382）本体只有 `Marshal.PtrToStructure` + 几个分支判断；
而且那个会话的配置是 `中键=False`，`HandleMouseMove`（:514）第一行就 return。
由此可见 20~40ms 不是回调里的代码慢，而是**持有钩子的线程被 GC / CPU 争抢打断**
（该时间窗口你大概率正在编译或跑测试，机器满载）。

但这条告警仍有两点值得改：

1. **告警信息缺事件类型**：现在只写“鼠标/键盘”，无法区分是 `WM_MOUSEMOVE` 还是 `WM_MBUTTONDOWN`。
   把 `msg` 一起写进日志，下次就能直接定位。
2. **缺根治手段**：`WH_MOUSE_LL` 回调运行在**安装钩子的线程**上（这里是 WPF UI 线程），
   UI 一忙，**全系统**的键鼠都会延迟；Windows 的 `LowLevelHooksTimeout` 默认 300ms，
   逼近就会被系统静默摘钩（表现为“热键突然全失效”）。
   要彻底免疫，应把 `SetWindowsHookEx` 放到**专用线程 + 自己的消息泵**（`Thread` + `GetMessage` 循环），
   与 WPF 的渲染/布局完全隔离——这是同类 WPF 钩子程序的标准做法。

### B5（P2）`Logger.Write` 的同步文件 I/O 在 UI 线程上 —— `Logger.cs:62-86`

```csharp
lock (Gate)
{
    RotateIfNeeded();
    File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8);
}
```

每次日志都是一次“打开→写→关闭”的同步磁盘操作，且在全局锁内。而很多日志是通过
`InputHookService.Post()` 投递到 Dispatcher 上执行的（Logger.cs 的 `EntryWritten` 也一样），
**就在 UI 线程**。Debug 级别（`RADIALLAUNCHER_LOGLEVEL=Debug`）下会有量级更大的开销，
既拖慢 UI 也给钩子延迟加码。

**建议**：常驻 `StreamWriter`（AutoFlush=false）+ 后台线程/队列批量落盘；`ReadTail`/`Clear` 配合 flush。

### B6（P3）`ApplyConfig()` 重复执行、重复日志

`SettingsWindow.OnSaveAndClose`（SettingsWindow.xaml.cs:1396）调一次 `_host.ApplyConfig()`，
窗口 `Close()` 后 `MainWindow.OpenSettingsWindow` 的 `Closed` 回调（MainWindow.xaml.cs:436）又调一次，
于是日志里出现成对的

```
18:31:10.583 界面字体：…      18:31:10.587 配置已应用
18:31:10.678 界面字体：…      18:31:10.679 配置已应用
```

每次都要重新 `FontCatalog.Resolve` + 打日志。建议在 `ApplyConfig()` 里做“值没变就直接返回”的去重，
或让 `Closed` 回调只在配置真的被替换过时才 `ApplyConfig()`。

### B7（P3）迁移判据的边界

`LooksLikeLegacy`（ConfigService.cs:215-229）以“既没有 `Trigger` 也没有 `Appearance` 分组”判定 v1。
用户手改出一个**缺 `Trigger` 分组但 `SchemaVersion=2`** 的文件时，会误走迁移路径：
`Migrate` 用 `LegacyConfig` 只读顶层字段，读不到 `Appearance` 分组里的颜色/尺寸，
于是 `result` 保留默认值 → **配色被重置**（`WheelItems` 因顶层同名侥幸保住）。

**建议**：迁移改成字段级合并——先以 `alreadyParsed` 为基底，legacy 取到值才覆盖。

---

## 4. 建议的修复优先级

| 优先级 | 事项 | 位置 |
|---|---|---|
| P1 | 测试目录重定向（治本，消除全部噪音与丢配置风险） | ConfigService.cs:21-25、Logger.cs:46-50、ConfigServiceRoundTripTests |
| P1 | `Migrate()` 失败改为返回 `alreadyParsed` | ConfigService.cs:280-284 |
| P1 | 迁移日志改为不带错误版本号 | ConfigService.cs:59 |
| P2 | 损坏时先尝试 `config.backup.json` 恢复 | ConfigService.cs:71-76 |
| P2 | 钩子告警带事件类型；长期：钩子移入专用线程 | InputHookService.cs:157-167 / 175-200 |
| P2 | Logger 改异步/常驻 StreamWriter | Logger.cs:62-103 |
| P3 | `ApplyConfig()` 去重 | MainWindow.xaml.cs:185-203 |

---

## 5. 未列入本报告的已确认正常项

- `selftest.log`：快捷键目录 228 条 / 14 分类 / 0 条解析失败，本地化键 162 处绑定、199 个定义、0 缺失，
  8 个窗口全部通过 —— 与 `--selftest` 的输出一致，没有问题。
- 正常会话（18:28:28、20:04:12）的启动链路完整无误：主窗口隐藏 → 托盘图标 → 键盘/鼠标钩子 →
  服务已启动 → 实例激活监听 → 轮盘唤出。
- `config.json` / `config.backup.json` 本身是合法 JSON（缩进 + CRLF），SchemaVersion=2，三个默认 profile 完整。
