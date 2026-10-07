# Radial Launcher

按住 **Ctrl** / **Win** / **鼠标中键**，在鼠标位置唤出的轮盘启动器。

Windows 10 / 11 · .NET 8 · WPF · 免安装

[![最新版本](https://img.shields.io/github/v/release/GGGinnnnn/RadialLauncher?label=Release&color=0067C0)](../../releases)
[![下载量](https://img.shields.io/github/downloads/GGGinnnnn/RadialLauncher/total?label=Downloads&color=0067C0)](../../releases)

---

## 这是什么

一个常驻托盘的轮盘启动器。按住触发键，鼠标处弹出一个环形菜单，
移动鼠标选中某一项，松开触发键就执行——不用记快捷键，也不用离开鼠标。

```
        ┌─────────────────────────────────────┐
        │        按住 Ctrl 0.2 秒             │
        │              ↓                      │
        │   代理   github   原神   下载       │
        │      ╲    ┌───────┐    ╱            │
        │       ╲   │ 松开  │   ╱             │
        │        ╲  │ 执行  │  ╱              │
        │   任务管理 └───────┘ 桌面           │
        │        锁屏      剪贴板             │
        └─────────────────────────────────────┘
```

支持 **4 向 / 8 向**两种排布，支持**点击执行**（而不是松手执行），
支持给特定程序（浏览器、VS Code 等）配一套**专属轮盘**。

## 功能

- **触发环境** — Ctrl / Win / 鼠标中键可多选；按住时长可调；
  黑白名单按进程名或窗口标题决定哪些窗口能唤出；全屏程序（游戏）自动跳过。
- **7 种功能类型** — 启动应用 / 打开文件夹 / 打开网址 / Windows 快捷键 /
  执行命令 / 运行脚本 / 内置功能。
  内置 **228 条 Windows 快捷键**可直接挑，每项都能单独设置启动参数、
  工作目录、以管理员身份运行、隐藏窗口。
- **外观** — 四向或八向、五种颜色（带取色器，也能直接从屏幕上吸色）、
  大小、中心死区、透明度、发光、动画。
- **界面字体** — 内置微软雅黑 / Segoe UI / Arial / 原神字体 / Minecraft 字体等
  16 个推荐项，也会列出本机全部已装字体；没装的会标灰，选了也不会出问题
  （自动回退，不会变方框）。
- **中英双语** — 界面可切换中文 / English。

## 界面

设置面板按 Windows 11「设置」的样式做的：左侧可折叠的侧边栏导航
（折叠后只留图标，带缓动动画）、圆角卡片、Win11 拨动开关与圆环滑块。

## 快速开始

1. 到 [Releases](../../releases) 下载最新的 `RadialLauncher-vX.Y.7z`，
   解压到任意固定位置（**不要**放在临时目录）。
2. 双击 `RadialLauncher.exe`。程序不显示主窗口，只在托盘出现一个蓝色轮盘图标。
3. 在任意窗口按住 `Ctrl` 约 0.2 秒 → 轮盘弹出 → 移动鼠标选中 → 松开执行。
   按 `Esc` 取消。
4. 右键托盘图标打开菜单，双击托盘图标直接打开设置面板。

> 压缩包是**自包含**的，已经带了完整的 .NET 运行时，不需要另外安装 .NET。

## 命令行参数

| 参数 | 说明 |
| --- | --- |
| （无） | 正常启动（单实例） |
| `--settings` | 启动后直接打开设置面板 |
| `--force` | 忽略单实例检测，强制再启动一个实例 |
| `--quit` | 让正在运行的实例优雅退出 |
| `--selftest` | 自检：加载全部窗口、校验快捷键目录与本地化键，结果写入 `selftest.log` |

## 数据目录

```
%AppData%\RadialLauncher\
    config.json          全部设置（可直接备份 / 迁移）
    RadialLauncher.log   运行日志，出问题先看这里
    tray.ico             托盘图标
```

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
dotnet build RadialLauncher/RadialLauncher.csproj -c Release

# 自包含发布（和 Release 里的包一致）
dotnet publish RadialLauncher/RadialLauncher.csproj \
  -c Release -r win-x64 --self-contained true -o publish
```

用 Visual Studio 或 Rider 直接打开 `RadialLauncher.slnx` 也可以。

## 项目结构

```
RadialLauncher/
├─ Models/        AppConfig 等数据模型
├─ Services/      钩子引擎、配置、本地化、字体目录、动作执行 …
│    InputHookService.cs   低级键鼠钩子（全局触发）
│    ConfigService.cs      配置读写与版本迁移
│    FontCatalog.cs        界面字体探测与回退
├─ Views/         SettingsWindow / WheelWindow / 取色器 …
├─ Controls/      自绘控件（轮盘预览等）
├─ Styles/        Theme.xaml（设计标记）+ Controls.xaml（控件外观）
└─ Resources/     图标
```

界面外观集中在 `Styles/` 下的两个资源字典里，想改配色或控件样式只动这两处即可。

## 实现要点

- **钩子回调必须立刻返回。** 早期版本在钩子回调里直接执行用户动作
  （打开网址、发快捷键、写日志），这类操作会阻塞钩子线程，最坏情况与系统输入链
  互相等待造成死锁——表现为托盘无响应、程序打不开、后台留下一个连"结束任务"
  都杀不掉的僵尸进程。现在回调里**只改状态标志**，所有实际动作都通过消息循环
  异步执行，实测钩子回调最长不到 9 ms（系统上限 300 ms）。
- **执行动作前会等修饰键抬起。** 触发键本身就是修饰键，而松开触发键到合成快捷键
  之间，Windows 的按键状态表对物理按键的更新滞后于低级钩子，于是 `Win+V`
  会被系统算成 `Ctrl+Win+V`（还伴随系统提示音）。现在会先确认
  Ctrl / Shift / Alt / Win 都已抬起再执行。
- **启动兜底。** 新实例会先向已有实例发一个"弹出设置面板"的请求，收到回执说明
  对方活着；收不到（说明是卡死的残留进程）就直接自己启动，不会被挡在门外。

## 许可

尚未指定许可证。
