<img width="385" height="412" alt="808914e4f3b592655551ac28212aafe8" src="https://github.com/user-attachments/assets/8dda3e1c-3612-48e3-8f54-35eed34e12fb" />
# Radial Launcher

按住 **Ctrl** / **Win** / **Alt** / **鼠标中键**，或**任意自定义按键**，在鼠标位置唤出的轮盘启动器。

Windows 10 / 11（64 位）· .NET 8 · WPF · 免安装

[![最新版本](https://img.shields.io/github/v/release/GGGinnnnn/RadialLauncher?label=Release&color=0067C0)](../../releases)
[![下载量](https://img.shields.io/github/downloads/GGGinnnnn/RadialLauncher/total?label=Downloads&color=0067C0)](../../releases)
[![许可证](https://img.shields.io/github/license/GGGinnnnn/RadialLauncher?label=License&color=0067C0)](LICENSE)

---

## 这是什么

一个常驻托盘的轮盘启动器。按住触发键，鼠标处弹出一个环形菜单，
移动鼠标选中某一项，松开触发键就执行——不用记快捷键，也不用离开鼠标。

```
<img width="385" height="412" alt="808914e4f3b592655551ac28212aafe8" src="https://github.com/user-attachments/assets/aab42d59-50d0-42f2-b167-fcfe635970cb" />

```

支持 **4 向 / 8 向**两种排布，支持**点击执行**（而不是松手执行），
支持给特定程序（浏览器、VS Code 等）配一套**专属轮盘**。

## 功能

- **触发环境** — Ctrl / Win / Alt / 鼠标中键可多选，还能挑任意单键（Shift、F13、CapsLock…）当触发键；
  按住时长可调；
  黑白名单按进程名或窗口标题决定哪些窗口能唤出；全屏程序（游戏）自动跳过。
- **7 种功能类型** — 启动应用 / 打开文件夹 / 打开网址 / Windows 快捷键 /
  执行命令 / 运行脚本 / 内置功能。
  内置 **228 条 Windows 快捷键**可直接挑，每项都能单独设置启动参数、
  工作目录、以管理员身份运行、隐藏窗口。
- **外观** — 四向或八向、五种颜色（带取色器，也能直接从屏幕上吸色）、
  大小、中心死区、透明度、发光、动画。
- **界面字体** — 内置微软雅黑 / Segoe UI / Arial / 原神字体 / Minecraft 字体等
  17 个推荐项，也会列出本机全部已装字体；没装的会标灰，选了也不会出问题
  （自动回退，不会变方框）。
- **中英双语** — 界面可切换中文 / English。

## 界面

设置面板按 Windows 11「设置」的样式做的：左侧可折叠的侧边栏导航
（折叠后只留图标，带缓动动画）、圆角卡片、Win11 拨动开关与圆环滑块。

## 快速开始

到 [Releases](../../releases) 下载。四种包功能完全一样，只是打包方式不同：

| 形态 | 体积 | 要装 .NET 吗 | 说明 |
| --- | --- | --- | --- |
| **单文件 exe**（推荐） | 约 64 MB | 不用 | 只有一个文件，双击即用，最省事 |
| 单文件 exe（不含运行时） | 约 1.8 MB | **要装 .NET 8 桌面运行时** | 体积只有 1/35，适合已装运行时、想省流量的人 |
| 便携 7z | 约 46 MB | 不用 | 解压成文件夹，便于留档 / 带走 |
| MSIX 安装包 | 约 64 MB | 不用 | 进开始菜单，可从「应用」里卸载 |

**① 单文件版（推荐）** —— 下载 `RadialLauncher-v0.6.exe`，放到任意固定目录
（**不要**放临时目录），双击运行。程序不显示主窗口，只在托盘出现一个蓝色轮盘图标。
首次启动会先自解压到 `%TEMP%\.net\`，慢一两秒，之后就正常了；在 exe 旁边放一个空的
`portable.marker` 就能切成便携模式（见「数据目录」）。

**② 单文件版 · 不含运行时** —— 下载 `RadialLauncher-v0.6-noruntime.exe`，用法同上，
但**不带 .NET 运行时**，所以整包只有约 1.8 MB。

动手前先确认目标机装没装 .NET 8 桌面运行时：

```powershell
dotnet --list-runtimes | Select-String WindowsDesktop
# 有 Microsoft.WindowsDesktop.App 8.x 就行
```

没装的话到 [.NET 8 下载页](https://dotnet.microsoft.com/download/dotnet/8.0)
装 **Desktop Runtime (x64)**（不是 ASP.NET Core Runtime）。缺运行时不会有任何提示文字，
只会弹一个 "To run this application, you must install .NET Desktop Runtime" 的对话框 ——
看到它就说明装错了包，换 ① 即可。

**③ 便携 7z** —— 下载 `RadialLauncher-v0.6-portable.7z`，解压到任意固定位置，
双击里面的 `RadialLauncher.exe`。

**④ MSIX 安装包** —— 下载 `RadialLauncher_0.6.0.0_x64.msix` 和配套的
`RadialLauncher_Test.cer`，**先把 `.cer` 导入「本地计算机 → 受信任人」**
（`LocalMachine\TrustedPeople`，导到「当前用户」不管用），再双击 `.msix` 安装。

> 除了 ②，其余三种都是**自包含**的，已经带了完整的 .NET 运行时，不需要另外安装 .NET。
> ② 是同一个程序的"瘦身版"：省掉运行时那一份体积，代价是目标机得自己先装好。

**上手**：在任意窗口按住 `Ctrl` 约 0.2 秒 → 轮盘弹出 → 移动鼠标选中 → 松开执行，
按 `Esc` 取消。右键托盘图标打开菜单，双击托盘图标直接打开设置面板。

## 命令行参数

| 参数 | 说明 |
| --- | --- |
| （无） | 正常启动（单实例） |
| `--settings` | 启动后直接打开设置面板 |
| `--force` | 忽略单实例检测，强制再启动一个实例 |
| `--quit` | 让正在运行的实例优雅退出 |
| `--selftest` | 自检：加载全部窗口、校验快捷键目录与本地化键，结果写入 `selftest.log` |

## 数据目录

默认在 `%AppData%\RadialLauncher\`：

```
config.json               全部设置（可直接备份 / 迁移）
config.backup.json        上一次保存的副本，config.json 读不出来时自动用它恢复
config.corrupt-*.json     配置真的坏了才会出现的取证文件
RadialLauncher.log        运行日志，出问题先看这里
RadialLauncher.log.old    日志超过 1 MB 后的上一份
tray.ico                  托盘图标
```

**也可以改用便携模式**，把配置、日志与托盘图标全部留在 exe 同级目录，
整个文件夹拷到 U 盘就能带走全部设置。两种做法：手动在 exe 旁边放一个空的
`portable.marker`，或者在「系统设置 → 数据目录」里切换。切换是**复制**而不是搬移
（原目录保留），并且立即生效。环境变量 `RADIALLAUNCHER_DATA_DIR` 可以硬覆盖
数据目录，优先级最高。

程序装在 `Program Files` 这类受保护目录时请用默认的 `%AppData%` ——
便携目录写不进去，设置面板会直接告诉你原因。

卸载：删除程序文件夹，再删掉上面这个数据目录即可。**不写注册表**
（除非你开启过「开机自启动」）。

## 已知限制

- **开启 Win 键触发后，单独轻点 Win 不再弹出开始菜单。**
  这是 Windows 11 的限制——系统会忽略程序合成的 Win 键。
  需要开始菜单时请按 `Ctrl+Esc` 或点任务栏的开始按钮；按住 Win 唤出轮盘不受影响。
- **鼠标中键是"迟到才接管"**：按下时完全不拦截，轻点和按住拖动滚动都与
  没装本程序时一模一样，只有按住超过设定时长才唤出轮盘。接管瞬间会补发一次
  "中键松开"，所以如果那一刻光标正好停在链接或浏览器标签页上，程序可能把它
  当成一次中键单击。可在设置里关掉这个补发，代价是浏览器可能停留在中键滚动模式。
- 开启「以管理员身份运行」后，每次启动都会弹 UAC 提权确认。
- 设置面板打开期间全局钩子会暂停，避免在输入框里打字被拦截。

## 从源码构建

需要 .NET 8 SDK（或更高版本的 SDK）。

```bash
git clone https://github.com/GGGinnnnn/RadialLauncher.git
cd RadialLauncher

dotnet build RadialLauncher/RadialLauncher.csproj -c Release              # 构建
dotnet test RadialLauncher.Tests/RadialLauncher.Tests.csproj -c Release   # 单元测试

# 散装自包含发布（和 Release 里的 7z 一致）
dotnet publish RadialLauncher/RadialLauncher.csproj \
  -c Release -r win-x64 --self-contained true -o build/publish/portable

# 单文件 exe：产物只有一个文件，约 140 MB
dotnet publish RadialLauncher/RadialLauncher.csproj \
  -c Release -r win-x64 -p:SingleFilePublish=true -o build/publish/single

# 单文件 exe · 不含运行时（和 Release 里的 -noruntime.exe 一致）：约 1.8 MB
dotnet publish RadialLauncher/RadialLauncher.csproj \
  -c Release -r win-x64 -p:SingleFilePublish=true -p:NoRuntimePublish=true \
  -o build/publish/noruntime
```

发布形态开关（注释就在 `RadialLauncher.csproj` 里）：

| 开关 | 效果 |
| --- | --- |
| `-p:SingleFilePublish=true` | 打成单文件，约 140 MB |
| `-p:SingleFileCompress=true` | 再压到约 64 MB，代价是首次启动多约 1 秒解压 |
| `-p:NoRuntimePublish=true` | **不含运行时**：单文件约 1.8 MB，散装约 1.8 MB（8 个文件）。目标机必须预装 .NET 8 桌面运行时 |

`NoRuntimePublish` 会覆盖 `SingleFilePublish` 里默认打开的 `SelfContained`，所以两个开关
可以叠着用 —— 上面那条命令出来的就是单文件的瘦身版。它本质上是
`-p:SelfContained=false` 换了个名字（后者照旧可用），起名字只是为了让"不含运行时"
能像单文件一样、当成一个有名有姓的发布形态来记，不用每次去记该关哪个底层属性。

用 Visual Studio 或 Rider 直接打开 `RadialLauncher.slnx` 也可以。
`MSIX Packaging/` 是 MSIX 打包工程，`dotnet` CLI 编不动它（缺 DesktopBridge 任务），
所以 `dotnet build RadialLauncher.slnx` 会在这一项上报错——构建应用请直接指定
`RadialLauncher/RadialLauncher.csproj`；要出 MSIX 就用装了「Windows 应用打包项目」
组件的 Visual Studio。MSIX 用自签证书签名（证书是签名时生成的，不进版本库），
**安装前必须先把那个 `.cer` 导入「本地计算机 → 受信任人」**
（`LocalMachine\TrustedPeople` —— 导到「当前用户」不管用）。

## 项目结构

```
RadialLauncher/
├─ Models/        AppConfig 等数据模型
├─ Services/      钩子引擎、配置、本地化、字体目录、动作执行 …
│    InputHookService.cs    低级键鼠钩子（跑在专用线程 + 自有消息泵上）
│    HookGestureMachine.cs  手势状态机（纯逻辑，可单元测试）
│    ConfigService.cs       配置读写、版本迁移、损坏时从备份恢复
│    AppPaths.cs            数据目录解析（%AppData% / 便携 / 环境变量）
│    WheelGeometry.cs       轮盘几何计算（窗口与预览共用一套）
│    FontCatalog.cs         界面字体探测与回退链
├─ Views/         SettingsWindow / WheelWindow / 取色器 …
├─ Controls/      自绘控件（轮盘预览等）
├─ Styles/        Theme.xaml（设计标记）+ Controls.xaml（控件外观）
└─ Resources/     图标

RadialLauncher.Tests/   xUnit 单元测试（约 450 个）
```

界面外观集中在 `Styles/` 下的两个资源字典里，想改配色或控件样式只动这两处即可。

## 实现要点

- **钩子回调只改状态标志，而且跑在自己的线程上。** 早期版本在钩子回调里直接执行用户动作
  （打开网址、发快捷键、写日志），这类操作会阻塞钩子线程，最坏情况与系统输入链
  互相等待造成死锁——表现为托盘无响应、程序打不开、后台留下一个连"结束任务"
  都杀不掉的僵尸进程。现在钩子装在一条**专用线程**上（自带消息泵，与界面渲染完全隔离），
  回调里**只改状态标志**，所有实际动作都异步执行；系统对钩子回调有 300 ms 硬上限，
  超时就会被静默摘钩，这条线程的存在就是为了远离那条线。
- **执行动作前会等修饰键抬起。** 触发键本身就是修饰键，而松开触发键到合成快捷键
  之间，Windows 的按键状态表对物理按键的更新滞后于低级钩子，于是 `Win+V`
  会被系统算成 `Ctrl+Win+V`（还伴随系统提示音）。现在会先确认
  Ctrl / Shift / Alt / Win 都已抬起再执行。
- **启动兜底。** 新实例会先向已有实例发一个"弹出设置面板"的请求，收到回执说明
  对方活着；收不到（说明是卡死的残留进程）就直接自己启动，不会被挡在门外。

## 许可

本项目采用 **GNU 通用公共许可证第 2 版（GPL-2.0）**，全文见 [LICENSE](LICENSE)。

你可以自由地使用、修改、再分发，前提是分发时**一并提供源代码**、保留同样的许可，
并且不附加任何额外限制。本程序**不含任何担保**。

> 发布的是二进制包（单文件 exe / 单文件 exe 不含运行时 / 便携 7z / MSIX），完整源码就在本仓库。
