using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace RadialLauncher.Services
{
    /// <summary>
    /// 极简本地化。XAML 里用
    /// <c>{Binding [Trigger_Mode], Source={x:Static services:Loc.Instance}}</c>
    /// 绑定，切语言时统一刷新。
    /// 键名只用下划线，不要用点号（WPF 索引器路径不认点号）。
    /// </summary>
    public sealed class Loc : INotifyPropertyChanged
    {
        // 用 Lazy 而不是直接 new()：
        // 静态字段初始化按文本顺序执行，而语言表 Zh/En 声明在本类末尾，
        // 若在这里直接 new()，构造函数里的 _table = Zh 会拿到还没初始化的 null。
        // Lazy 的工厂函数在类型初始化完成之后才运行，因此永远安全。
        private static readonly Lazy<Loc> LazyInstance = new(() => new Loc());

        public static Loc Instance => LazyInstance.Value;

        public event PropertyChangedEventHandler? PropertyChanged;

        private string _language = "zh-CN";
        private Dictionary<string, string> _table = Zh;

        private Loc() { }

        public string Language => _language;

        public bool IsEnglish => _language.StartsWith("en", StringComparison.OrdinalIgnoreCase);

        /// <summary>当前语言字符串；找不到键时回退到中文，再回退到键名。</summary>
        public string this[string key]
        {
            get
            {
                if (string.IsNullOrEmpty(key)) return "";
                if (_table.TryGetValue(key, out var value)) return value;
                if (Zh.TryGetValue(key, out var fallback)) return fallback;
                return key;
            }
        }

        public void SetLanguage(string language)
        {
            var lang = string.IsNullOrWhiteSpace(language) ? "zh-CN" : language.Trim();
            if (!lang.StartsWith("en", StringComparison.OrdinalIgnoreCase)) lang = "zh-CN";

            if (string.Equals(lang, _language, StringComparison.OrdinalIgnoreCase)) return;

            _language = lang;
            _table = IsEnglish ? En : Zh;

            Logger.Info("界面语言切换为 " + _language);

            // "Item[]" 会让所有索引器绑定重新求值
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnglish)));
        }

        /// <summary>代码里取字符串。</summary>
        public static string T(string key) => Instance[key];

        /// <summary>某个键是否有翻译（自检用）。</summary>
        public static bool HasKey(string key)
            => !string.IsNullOrEmpty(key) && Zh.ContainsKey(key);

        /// <summary>全部已知的键（自检用）。</summary>
        public static IReadOnlyCollection<string> Keys => Zh.Keys;

        /// <summary>按语言二选一。</summary>
        public static string Pick(string zh, string en) => Instance.IsEnglish ? en : zh;

        // ============================================================
        private static readonly Dictionary<string, string> Zh = new()
        {
            // 通用
            ["App_Title"] = "轮盘启动器 设置",
            ["Common_Save"] = "保存",
            ["Common_SaveClose"] = "保存并关闭",
            ["Common_Cancel"] = "取消",
            ["Common_OK"] = "确定",
            ["Common_Close"] = "关闭",
            ["Common_Apply"] = "应用",
            ["Common_Remove"] = "移除",
            ["Common_Add"] = "添加",
            ["Common_Refresh"] = "刷新",
            ["Common_None"] = "无",
            ["Common_Reset"] = "恢复默认",
            ["Common_Test"] = "测试",
            ["Common_Browse"] = "浏览…",
            ["Common_Yes"] = "是",
            ["Common_No"] = "否",
            ["Common_Enabled"] = "启用",
            ["Common_Disabled"] = "已禁用",

            // 标签页
            ["Tab_General"] = "总览",
            ["Tab_Trigger"] = "触发环境",
            ["Tab_Profiles"] = "应用专属",
            ["Tab_Appearance"] = "外观",
            ["Tab_Functions"] = "功能选择",
            ["Tab_System"] = "系统设置",
            ["Tab_About"] = "关于",

            // 侧边栏 / 页面副标题
            ["Nav_Toggle"] = "折叠或展开侧边栏（仅显示图标）",
            ["General_Subtitle"] = "运行状态与常用操作",
            ["Trigger_Subtitle"] = "设定什么时候、按什么键唤出轮盘",
            ["Profiles_Subtitle"] = "为特定应用准备一套专属轮盘",
            ["Appearance_Subtitle"] = "轮盘的方向、配色与大小",
            ["Functions_Subtitle"] = "配置每个轮盘槽位要执行的功能",
            ["System_Subtitle"] = "语言、字体、启动方式与项目信息",
            ["Appearance_Options"] = "其它选项",
            ["System_Font"] = "界面字体",
            ["System_FontReload"] = "刷新字体列表",
            ["System_FontHint"] = "列表中标注“未安装”的字体（例如原神、Minecraft 自带字体）需要先自行安装到系统，装好后点“刷新字体列表”即可选用。即使选了没装的字体也不会出问题，界面会自动回退到微软雅黑 / Segoe UI。",
            ["System_FontEffective"] = "当前实际生效：{0}",
            ["System_FontFallback"] = "{0} 未安装，已回退到 {1}",
            ["System_StartupGroup"] = "启动与权限",
            ["System_SaveHint"] = "改动会在点“保存并关闭”后生效",

            // 界面外观（深色模式 / 自定义背景 / 控件不透明度）
            ["System_AppearanceGroup"] = "界面外观",
            ["System_DarkMode"] = "深色模式",
            ["System_DarkModeHint"] = "选中后立即换成深色主题，不需要重启。",
            ["System_CustomBackground"] = "使用自定义背景图片（仅设置面板）",
            ["System_BackgroundChoose"] = "选择图片…",
            ["System_BackgroundClear"] = "清除",
            ["System_BackgroundNone"] = "（尚未选择图片）",
            ["System_BackgroundHint"] = "背景图只作用于设置面板，不影响轮盘与托盘。支持 png / jpg / bmp / gif / webp，会自动等比铺满。",
            ["System_PanelOpacity"] = "控件不透明度",
            ["System_PanelOpacityHint"] = "调低之后卡片和输入框会变透明，露出后面的背景图。",
            ["System_BackgroundMissing"] = "图片不存在或无法读取：{0}",

            // 测试模式
            ["System_TestMode"] = "测试模式（开发与上报错误时使用）",
            ["System_TestModeEnable"] = "启用测试模式（详细日志）",
            ["System_TestModeHint"] = "打开后每次启动都会在数据目录里另存一份详细日志：Debug 级、从启动第一行开始记，错误与警告分别标成 [ERROR] / [WARN ]。文件名包含日期、时间、启动参数以及是否以管理员运行。遇到问题把这个文件发出来即可。",
            ["System_OpenSessionLog"] = "打开本次详细日志",
            ["System_SessionLogActive"] = "本次运行的详细日志：{0}",
            ["System_SessionLogInactive"] = "本次运行未开启测试模式（打开开关并保存后，从下次启动开始记录）。",
            ["System_TestArgumentHint"] = "也可以在命令行加 --test 直接开启测试模式，用来抓“设置面板还没打开就出问题”的现场。",

            // 总览
            ["General_Status"] = "运行状态",
            ["General_EnableWheel"] = "启用轮盘（关闭后不再拦截任何按键）",
            ["General_TriggerKeys"] = "当前触发键",
            ["General_HoldTime"] = "按住时长",
            ["General_Environment"] = "触发环境",
            ["General_Foreground"] = "当前前台窗口",
            ["General_ForegroundDesktop"] = "桌面",
            ["General_ForegroundTaskbar"] = "任务栏",
            ["General_Hint"] = "提示：按住触发键约 {0} 毫秒即可在鼠标处唤出轮盘，松开触发键执行选中项，按 Esc 取消。",
            ["General_TestNow"] = "立即测试轮盘",
            ["General_OpenData"] = "打开配置目录",
            ["General_Reload"] = "重新加载配置",
            ["General_NotRunning"] = "已停止",
            ["General_Running"] = "运行中",
            ["General_ProfilesActive"] = "应用专属轮盘已启用",

            // 触发环境
            ["Trigger_Mode"] = "触发模式",
            ["Trigger_Blacklist"] = "黑名单（列表外的窗口都可触发）",
            ["Trigger_Whitelist"] = "白名单（仅列表内的窗口可触发）",
            ["Trigger_Keys"] = "触发键（可多选）",
            ["Trigger_Ctrl"] = "Ctrl 键",
            ["Trigger_Win"] = "Win 键",
            ["Trigger_Alt"] = "Alt 键",
            ["Trigger_Middle"] = "鼠标中键",
            ["Trigger_CustomKeys"] = "自定义触发键",
            ["Trigger_CustomPlaceholder"] = "选择或输入按键名，如 F13、CapsLock",
            ["Trigger_CustomAdd"] = "添加",
            ["Trigger_CustomRemove"] = "移除所选",
            ["Trigger_CustomHint"] = "自定义触发键与 Ctrl / Alt 一样只监听、不拦截：按住超过设定时长才唤出轮盘，按住期间同时按着其他键（含另一个触发键）则取消，组合键与打字都不受影响。该键自身的原有行为仍然发生，例如 CapsLock 仍会切换大小写。",
            ["Trigger_CustomInvalid"] = "无法识别的按键名（或该键已有专属用途）：{0}",
            ["Trigger_CustomDuplicate"] = "该按键已经在列表里：{0}",
            ["Trigger_CustomFull"] = "自定义触发键最多 {0} 个。",
            ["Trigger_Hold"] = "按住时长（毫秒）",
            ["Trigger_SuppressWin"] = "拦截 Win 键，避免唤出轮盘时弹出开始菜单",
            ["Trigger_SuppressMiddle"] = "接管中键时补发一次“松开”（推荐）",
            ["Trigger_CancelOnOtherKey"] = "按住期间同时按下其他键则取消（保护 Ctrl+C、Win+E 等组合键；键的先后顺序都算）",
            ["Trigger_SkipFullscreen"] = "全屏程序（游戏、全屏视频）中不触发",
            ["Trigger_WindowDetector"] = "窗口检测器",
            ["Trigger_Behavior"] = "触发行为",
            ["Trigger_AddBlack"] = "加入黑名单 →",
            ["Trigger_AddWhite"] = "加入白名单 →",
            ["Trigger_BlacklistTitle"] = "黑名单（不唤出轮盘）",
            ["Trigger_WhitelistTitle"] = "白名单（允许唤出轮盘）",
            ["Trigger_MiddleNote"] = "说明：中键按下时不会立刻拦截 —— 轻点和“按住拖动”滚动都与没装本程序时完全一样；只有按住超过设定时长才接管。接管瞬间会补发一次“中键松开”，因此如果那一刻光标正停在链接或标签页上，程序可能把它当成一次中键单击（新开或关闭标签）。",
            ["Trigger_CtrlNote"] = "说明：Ctrl 为纯监听，不会拦截，所有 Ctrl 组合键照常工作。",
            ["Trigger_AltNote"] = "说明：Alt 同样为纯监听，不会拦截，Alt+Tab、Alt+F4 等组合键照常工作。",
            ["Trigger_WinSuppressNote"] = "注意：拦截 Win 键后，单独轻点 Win 将不再弹出开始菜单（Windows 会忽略程序合成的 Win 键）。需要开始菜单时请用 Ctrl+Esc 或直接点任务栏开始按钮；按住 Win 唤出轮盘不受影响。",

            // 应用专属
            ["Profiles_Title"] = "应用专属轮盘",
            ["Profiles_Intro"] = "为特定应用配置专属功能。命中进程名时，唤出的轮盘会使用这里的选项。",
            ["Profiles_Enable"] = "启用应用专属轮盘",
            ["Profiles_List"] = "配置列表",
            ["Profiles_New"] = "新建",
            ["Profiles_FromCurrent"] = "从当前窗口新建",
            ["Profiles_Duplicate"] = "复制",
            ["Profiles_Delete"] = "删除",
            ["Profiles_Name"] = "名称",
            ["Profiles_Icon"] = "图标",
            ["Profiles_Processes"] = "匹配进程名（每行一个，不含 .exe）",
            ["Profiles_Titles"] = "标题关键字（可选，每行一个）",
            ["Profiles_Enabled"] = "启用此配置",
            ["Profiles_UseGlobal"] = "使用全局功能项（仅覆盖方向数）",
            ["Profiles_ItemCount"] = "方向数",
            ["Profiles_FollowGlobal"] = "跟随全局",
            ["Profiles_Items"] = "专属功能项",
            ["Profiles_SelectHint"] = "请在左侧选择一个配置",
            ["Profiles_DeleteConfirm"] = "确定删除配置“{0}”吗？",

            // 外观
            ["Appearance_SkinGroup"] = "轮盘皮肤",
            ["Appearance_SkinHint"] = "皮肤决定装饰风格（辐条、双环、渐变纵深）。切换皮肤会同时套用该皮肤的预设配色，套用之后仍可在下面继续微调颜色。",
            ["Icon_Genshin"] = "原神图标包（从游戏轮盘截图提取）",
            ["Appearance_Direction"] = "轮盘方向数",
            ["Appearance_Dir4"] = "四向",
            ["Appearance_Dir8"] = "八向",
            ["Appearance_Colors"] = "配色",
            ["Appearance_Background"] = "轮盘底色",
            ["Appearance_Border"] = "边框颜色",
            ["Appearance_Item"] = "选项底色",
            ["Appearance_ItemSelected"] = "选中底色",
            ["Appearance_Text"] = "文字 / 图标颜色",
            ["Appearance_Pick"] = "取色",
            ["Appearance_Eyedropper"] = "屏幕取色",
            ["Appearance_Preview"] = "实时预览",
            ["Appearance_Scale"] = "轮盘大小",
            ["Appearance_ItemSize"] = "选项大小",
            ["Appearance_DeadZone"] = "中心死区",
            ["Appearance_Opacity"] = "整体不透明度",
            ["Appearance_Glow"] = "显示外发光",
            ["Appearance_Animation"] = "唤出动画（淡入 + 缩放）",
            ["Appearance_ExecuteOnRelease"] = "松开触发键时执行选中项（关闭后需用鼠标点击）",
            ["Appearance_ResetColors"] = "恢复默认配色",

            // 功能选择
            ["Functions_Slots"] = "轮盘槽位",
            ["Functions_Slot"] = "槽位 {0}",
            ["Functions_Editor"] = "功能设置",
            ["Functions_Name"] = "名称",
            ["Functions_Icon"] = "图标",
            ["Functions_ActionType"] = "功能类型",
            ["Functions_Value"] = "目标（路径 / 网址 / 快捷键 / 命令）",
            ["Functions_Arguments"] = "启动参数",
            ["Functions_WorkingDir"] = "工作目录",
            ["Functions_RunAsAdmin"] = "以管理员身份运行",
            ["Functions_Hidden"] = "隐藏窗口",
            ["Functions_PickShortcut"] = "快捷键目录…",
            ["Functions_PickIcon"] = "选择图标…",
            ["Functions_TestRun"] = "测试执行",
            ["Functions_Clear"] = "清空此槽位",
            ["Functions_SelectHint"] = "请在左侧选择一个槽位",
            ["Functions_ShortcutMatched"] = "已匹配：{0}",
            ["Functions_ShortcutCustom"] = "自定义快捷键",
            ["Functions_SelectSlotFirst"] = "请先选择一个槽位",
            ["Functions_EmptySlot"] = "（空槽位）",

            // 功能类型
            ["Action_LaunchApp"] = "启动应用",
            ["Action_OpenFolder"] = "打开文件夹",
            ["Action_OpenUrl"] = "打开网址",
            ["Action_Shortcut"] = "Windows 快捷键",
            ["Action_Command"] = "执行命令",
            ["Action_Script"] = "运行脚本",
            ["Action_Builtin"] = "内置功能",

            // 系统设置
            ["System_Language"] = "界面语言",
            ["System_LanguageHint"] = "切换后界面立即生效。",
            ["System_AutoStart"] = "开机自启动",
            ["System_AutoStartHint"] = "使用当前用户注册表，不需要管理员权限。",
            ["System_RunAsAdmin"] = "以管理员身份运行",
            ["System_RunAsAdminHint"] = "重启程序后生效。开启后启动时会弹出 UAC 提权确认。",
            ["System_AlreadyAdmin"] = "当前已经是管理员权限。",
            ["System_NotAdminUser"] = "当前账户不在管理员组，无法提权。",
            ["System_TrayTip"] = "启动时显示托盘提示",
            ["System_GitHub"] = "GitHub 项目地址",
            ["System_OpenGitHub"] = "打开项目主页",
            ["System_DataFolder"] = "打开数据目录",
            ["System_Reset"] = "恢复出厂设置",
            ["System_ResetConfirm"] = "将清除全部设置（包含应用专属轮盘），确定继续吗？",
            ["System_ResetDone"] = "已恢复出厂设置。",

            // 数据目录
            ["System_DataDirGroup"] = "数据目录",
            ["System_DataDirAppData"] = "使用 %AppData%\\RadialLauncher（推荐）",
            ["System_DataDirPortable"] = "使用程序所在目录（便携 / 绿色版）",
            ["System_DataDirHint"] = "配置、日志与托盘图标都保存在这里。便携模式下所有文件都留在程序目录内，整个文件夹拷贝到 U 盘即可带走全部设置；程序安装在 Program Files 等受保护目录时请改用 %AppData%。切换会把现有文件复制过去（原目录保留），并立即生效。",
            ["System_DataDirCurrent"] = "当前生效：{0}",
            ["System_DataDirEnvOverride"] = "当前生效：{0} —— 已被环境变量 RADIALLAUNCHER_DATA_DIR 覆盖，下面的选择不会生效。",
            ["System_DataDirSwitchedTitle"] = "数据目录已切换",
            ["System_DataDirSwitchedBody"] = "已迁移 {0} 个文件到\n{1}",
            ["System_DataDirFailedTitle"] = "切换数据目录失败",
            ["System_DataDirFailedBody"] = "无法使用「{0}」：\n{1}",

            // 关于
            ["About_Version"] = "版本",
            ["About_Log"] = "运行日志",
            ["About_OpenLog"] = "打开日志文件",
            ["About_ClearLog"] = "清空日志",
            ["About_RefreshLog"] = "刷新",
            ["About_Description"] = "按住 Ctrl / Win / 鼠标中键，在鼠标处唤出轮盘启动器。",
            ["About_License"] = "GNU GPL v2.0 · 自由软件，不含任何担保",

            // 托盘
            ["Tray_Tooltip"] = "轮盘启动器 — 右键打开菜单",
            ["Tray_OpenSettings"] = "打开设置面板",
            ["Tray_Reload"] = "重新加载配置",
            ["Tray_Test"] = "测试轮盘",
            ["Tray_Log"] = "查看日志",
            ["Tray_Exit"] = "退出",

            // 轮盘窗口
            ["Wheel_Release"] = "松开执行",
            ["Wheel_Cancel"] = "Esc 取消",
            ["Wheel_Empty"] = "未配置功能",
            ["Wheel_Profile"] = "应用专属轮盘",

            // 取色器
            ["Color_Title"] = "选择颜色",
            ["Color_Hue"] = "色相",
            ["Color_Saturation"] = "饱和度",
            ["Color_Brightness"] = "明度",
            ["Color_Alpha"] = "不透明度",
            ["Color_Hex"] = "十六进制",
            ["Color_Eyedropper"] = "屏幕取色",
            ["Color_EyedropperHint"] = "移动鼠标取色，单击确认，按 Esc 取消",
            ["Color_Presets"] = "预设",
            ["Color_Preview"] = "预览",

            // 快捷键选择器
            ["Shortcut_Title"] = "选择 Windows 快捷键",
            ["Shortcut_Search"] = "搜索名称或按键…",
            ["Shortcut_Category"] = "分类",
            ["Shortcut_AllCategories"] = "全部分类",
            ["Shortcut_Keys"] = "按键",
            ["Shortcut_Name"] = "功能",
            ["Shortcut_Select"] = "使用此快捷键",

            // 图标选择器
            ["Icon_Title"] = "选择图标",
            ["Icon_Hint"] = "点击选择，或在下方直接输入任意 emoji / 字符。",
            ["Icon_Custom"] = "自定义字符",

            // 消息
            ["Msg_SaveFailed"] = "保存配置失败",
            ["Msg_ActionFailed"] = "执行失败",
            ["Msg_NotConfigured"] = "这个槽位还没有配置目标",
            ["Msg_InvalidShortcut"] = "无法识别的快捷键：{0}",
            ["Msg_FileNotFound"] = "文件不存在：{0}",
            ["Msg_Done"] = "执行完成",
            ["Msg_ActionBlockedModifier"] = "「{0}」未执行",
            ["Msg_ActionBlockedModifierHint"] = "修饰键一直未抬起，为避免发送被污染的快捷键已取消本次执行，请松开后再试。",
        };

        private static readonly Dictionary<string, string> En = new()
        {
            // Common
            ["App_Title"] = "Radial Launcher Settings",
            ["Common_Save"] = "Save",
            ["Common_SaveClose"] = "Save & Close",
            ["Common_Cancel"] = "Cancel",
            ["Common_OK"] = "OK",
            ["Common_Close"] = "Close",
            ["Common_Apply"] = "Apply",
            ["Common_Remove"] = "Remove",
            ["Common_Add"] = "Add",
            ["Common_Refresh"] = "Refresh",
            ["Common_None"] = "None",
            ["Common_Reset"] = "Reset",
            ["Common_Test"] = "Test",
            ["Common_Browse"] = "Browse…",
            ["Common_Yes"] = "Yes",
            ["Common_No"] = "No",
            ["Common_Enabled"] = "Enabled",
            ["Common_Disabled"] = "Disabled",

            // Tabs
            ["Tab_General"] = "Overview",
            ["Tab_Trigger"] = "Trigger",
            ["Tab_Profiles"] = "App Profiles",
            ["Tab_Appearance"] = "Appearance",
            ["Tab_Functions"] = "Functions",
            ["Tab_System"] = "System",
            ["Tab_About"] = "About",

            // Sidebar / page subtitles
            ["Nav_Toggle"] = "Collapse or expand the sidebar (icons only)",
            ["General_Subtitle"] = "Status and quick actions",
            ["Trigger_Subtitle"] = "Decide when and with which key the wheel opens",
            ["Profiles_Subtitle"] = "Give specific apps their own wheel",
            ["Appearance_Subtitle"] = "Directions, colours and size of the wheel",
            ["Functions_Subtitle"] = "Configure what each wheel slot does",
            ["System_Subtitle"] = "Language, font, startup and project info",
            ["Appearance_Options"] = "Other options",
            ["System_Font"] = "Interface font",
            ["System_FontReload"] = "Rescan fonts",
            ["System_FontHint"] = "Entries marked \"not installed\" (for example the fonts shipped with Genshin or Minecraft) must be installed on your system first; then press Rescan fonts. Picking a missing font is harmless — the UI falls back to Microsoft YaHei / Segoe UI.",
            ["System_FontEffective"] = "Currently in effect: {0}",
            ["System_FontFallback"] = "{0} is not installed, fell back to {1}",
            ["System_StartupGroup"] = "Startup and privileges",
            ["System_SaveHint"] = "Changes take effect when you press Save & Close",

            // Appearance (dark mode / custom background / control opacity)
            ["System_AppearanceGroup"] = "Appearance",
            ["System_DarkMode"] = "Dark mode",
            ["System_DarkModeHint"] = "Switches to the dark theme immediately, no restart needed.",
            ["System_CustomBackground"] = "Use a custom background image (settings panel only)",
            ["System_BackgroundChoose"] = "Choose image…",
            ["System_BackgroundClear"] = "Clear",
            ["System_BackgroundNone"] = "(no image selected)",
            ["System_BackgroundHint"] = "The background only affects the settings panel, not the wheel or the tray. PNG / JPG / BMP / GIF / WEBP are supported and scaled to fill.",
            ["System_PanelOpacity"] = "Control opacity",
            ["System_PanelOpacityHint"] = "Lower it to make cards and inputs translucent so the background shows through.",
            ["System_BackgroundMissing"] = "Image is missing or unreadable: {0}",

            // Test mode
            ["System_TestMode"] = "Test mode (for development and bug reports)",
            ["System_TestModeEnable"] = "Enable test mode (detailed log)",
            ["System_TestModeHint"] = "When on, every launch writes an extra detailed log into the data folder: Debug level, from the very first line, with errors and warnings tagged as [ERROR] / [WARN ]. The file name contains date, time, command-line arguments and whether it ran elevated. Attach that file when reporting a problem.",
            ["System_OpenSessionLog"] = "Open this run's detailed log",
            ["System_SessionLogActive"] = "Detailed log for this run: {0}",
            ["System_SessionLogInactive"] = "Test mode is off for this run (turn the switch on and save; logging starts from the next launch).",
            ["System_TestArgumentHint"] = "You can also pass --test on the command line to enable test mode directly — useful for capturing problems that happen before the settings panel is open.",

            // Overview
            ["General_Status"] = "Status",
            ["General_EnableWheel"] = "Enable the wheel (when off, no key is intercepted)",
            ["General_TriggerKeys"] = "Trigger keys",
            ["General_HoldTime"] = "Hold time",
            ["General_Environment"] = "Trigger environment",
            ["General_Foreground"] = "Foreground window",
            ["General_ForegroundDesktop"] = "Desktop",
            ["General_ForegroundTaskbar"] = "Taskbar",
            ["General_Hint"] = "Hold a trigger key for about {0} ms to open the wheel at the cursor. Release to run the selected item, press Esc to cancel.",
            ["General_TestNow"] = "Test the wheel now",
            ["General_OpenData"] = "Open config folder",
            ["General_Reload"] = "Reload config",
            ["General_NotRunning"] = "Stopped",
            ["General_Running"] = "Running",
            ["General_ProfilesActive"] = "App profiles are enabled",

            // Trigger
            ["Trigger_Mode"] = "Mode",
            ["Trigger_Blacklist"] = "Blacklist (every window except the listed ones)",
            ["Trigger_Whitelist"] = "Whitelist (only the listed windows)",
            ["Trigger_Keys"] = "Trigger keys (multiple allowed)",
            ["Trigger_Ctrl"] = "Ctrl key",
            ["Trigger_Win"] = "Win key",
            ["Trigger_Alt"] = "Alt key",
            ["Trigger_Middle"] = "Middle mouse button",
            ["Trigger_CustomKeys"] = "Custom trigger keys",
            ["Trigger_CustomPlaceholder"] = "Pick or type a key name, e.g. F13, CapsLock",
            ["Trigger_CustomAdd"] = "Add",
            ["Trigger_CustomRemove"] = "Remove selected",
            ["Trigger_CustomHint"] = "Custom trigger keys are only observed, never intercepted — just like Ctrl/Alt. Hold one past the threshold to open the wheel; pressing another key while holding cancels it, so shortcuts and typing keep working. The key's own behavior still happens (CapsLock still toggles, for example).",
            ["Trigger_CustomInvalid"] = "Unrecognized key name (or that key already has a dedicated role): {0}",
            ["Trigger_CustomDuplicate"] = "That key is already in the list: {0}",
            ["Trigger_CustomFull"] = "At most {0} custom trigger keys.",
            ["Trigger_Hold"] = "Hold time (ms)",
            ["Trigger_SuppressWin"] = "Intercept the Win key so the Start menu does not pop up",
            ["Trigger_SuppressMiddle"] = "Send one middle-button release when taking over (recommended)",
            ["Trigger_CancelOnOtherKey"] = "Cancel if another key is held with the trigger (keeps Ctrl+C, Win+E working, in any press order)",
            ["Trigger_SkipFullscreen"] = "Do not trigger in fullscreen apps (games, fullscreen video)",
            ["Trigger_WindowDetector"] = "Window detector",
            ["Trigger_Behavior"] = "Trigger behavior",
            ["Trigger_AddBlack"] = "Add to blacklist →",
            ["Trigger_AddWhite"] = "Add to whitelist →",
            ["Trigger_BlacklistTitle"] = "Blacklist (no wheel)",
            ["Trigger_WhitelistTitle"] = "Whitelist (wheel allowed)",
            ["Trigger_MiddleNote"] = "Note: the middle button is NOT intercepted on press — taps and middle-drag scrolling behave exactly as if this app were not running. Only a hold beyond the threshold takes over, and at that instant one middle-button release is sent; so if the cursor sits on a link or a tab right then, the app may treat it as a middle click (open or close a tab).",
            ["Trigger_CtrlNote"] = "Note: Ctrl is only observed, never intercepted, so every Ctrl shortcut keeps working.",
            ["Trigger_AltNote"] = "Note: Alt is also only observed, never intercepted, so Alt+Tab, Alt+F4 and other shortcuts keep working.",
            ["Trigger_WinSuppressNote"] = "Heads-up: once the Win key is intercepted, a lone Win tap no longer opens the Start menu (Windows ignores synthesized Win keys). Use Ctrl+Esc or the taskbar Start button instead; holding Win to open the wheel still works.",

            // Profiles
            ["Profiles_Title"] = "App-specific wheels",
            ["Profiles_Intro"] = "Give specific apps their own wheel. When the process name matches, the wheel uses the items configured here.",
            ["Profiles_Enable"] = "Enable app-specific wheels",
            ["Profiles_List"] = "Profiles",
            ["Profiles_New"] = "New",
            ["Profiles_FromCurrent"] = "New from foreground",
            ["Profiles_Duplicate"] = "Duplicate",
            ["Profiles_Delete"] = "Delete",
            ["Profiles_Name"] = "Name",
            ["Profiles_Icon"] = "Icon",
            ["Profiles_Processes"] = "Process names (one per line, without .exe)",
            ["Profiles_Titles"] = "Title keywords (optional, one per line)",
            ["Profiles_Enabled"] = "Enable this profile",
            ["Profiles_UseGlobal"] = "Use the global items (only override the direction count)",
            ["Profiles_ItemCount"] = "Directions",
            ["Profiles_FollowGlobal"] = "Follow global",
            ["Profiles_Items"] = "Profile items",
            ["Profiles_SelectHint"] = "Select a profile on the left",
            ["Profiles_DeleteConfirm"] = "Delete the profile \"{0}\"?",

            // Appearance
            ["Appearance_SkinGroup"] = "Wheel skin",
            ["Appearance_SkinHint"] = "The skin controls the decoration style (spokes, double ring, gradient depth). Switching also applies that skin's preset colours — you can still fine-tune them below.",
            ["Icon_Genshin"] = "Genshin icon pack (extracted from the in-game wheel)",
            ["Appearance_Direction"] = "Directions",
            ["Appearance_Dir4"] = "Four-way",
            ["Appearance_Dir8"] = "Eight-way",
            ["Appearance_Colors"] = "Colors",
            ["Appearance_Background"] = "Wheel background",
            ["Appearance_Border"] = "Border",
            ["Appearance_Item"] = "Item background",
            ["Appearance_ItemSelected"] = "Selected item",
            ["Appearance_Text"] = "Text / icon color",
            ["Appearance_Pick"] = "Pick",
            ["Appearance_Eyedropper"] = "Screen picker",
            ["Appearance_Preview"] = "Live preview",
            ["Appearance_Scale"] = "Wheel size",
            ["Appearance_ItemSize"] = "Item size",
            ["Appearance_DeadZone"] = "Center dead zone",
            ["Appearance_Opacity"] = "Overall opacity",
            ["Appearance_Glow"] = "Show outer glow",
            ["Appearance_Animation"] = "Open animation (fade + scale)",
            ["Appearance_ExecuteOnRelease"] = "Run the selected item when the trigger key is released (otherwise click it)",

            ["Appearance_ResetColors"] = "Restore default colors",

            // Functions
            ["Functions_Slots"] = "Wheel slots",
            ["Functions_Slot"] = "Slot {0}",
            ["Functions_Editor"] = "Slot settings",
            ["Functions_Name"] = "Name",
            ["Functions_Icon"] = "Icon",
            ["Functions_ActionType"] = "Action type",
            ["Functions_Value"] = "Target (path / URL / shortcut / command)",
            ["Functions_Arguments"] = "Arguments",
            ["Functions_WorkingDir"] = "Working directory",
            ["Functions_RunAsAdmin"] = "Run as administrator",
            ["Functions_Hidden"] = "Hide window",
            ["Functions_PickShortcut"] = "Shortcut catalog…",
            ["Functions_PickIcon"] = "Pick icon…",
            ["Functions_TestRun"] = "Run once",
            ["Functions_Clear"] = "Clear this slot",
            ["Functions_SelectHint"] = "Select a slot on the left",
            ["Functions_ShortcutMatched"] = "Matched: {0}",
            ["Functions_ShortcutCustom"] = "Custom shortcut",
            ["Functions_SelectSlotFirst"] = "Select a slot first",
            ["Functions_EmptySlot"] = "(empty slot)",

            // Action types
            ["Action_LaunchApp"] = "Launch application",
            ["Action_OpenFolder"] = "Open folder",
            ["Action_OpenUrl"] = "Open URL",
            ["Action_Shortcut"] = "Windows shortcut",
            ["Action_Command"] = "Run command",
            ["Action_Script"] = "Run script",
            ["Action_Builtin"] = "Built-in action",

            // System
            ["System_Language"] = "Language",
            ["System_LanguageHint"] = "The UI updates immediately.",
            ["System_AutoStart"] = "Start with Windows",
            ["System_AutoStartHint"] = "Uses the current-user registry key; no admin rights required.",
            ["System_RunAsAdmin"] = "Run as administrator",
            ["System_RunAsAdminHint"] = "Takes effect after a restart. A UAC prompt will appear on startup.",
            ["System_AlreadyAdmin"] = "Already running with administrator rights.",
            ["System_NotAdminUser"] = "This account is not in the Administrators group.",
            ["System_TrayTip"] = "Show a tray notification on startup",
            ["System_GitHub"] = "GitHub project URL",
            ["System_OpenGitHub"] = "Open the project page",
            ["System_DataFolder"] = "Open data folder",
            ["System_Reset"] = "Factory reset",
            ["System_ResetConfirm"] = "This clears every setting (including app profiles). Continue?",
            ["System_ResetDone"] = "Settings restored to defaults.",

            // Data folder
            ["System_DataDirGroup"] = "Data folder",
            ["System_DataDirAppData"] = "Use %AppData%\\RadialLauncher (recommended)",
            ["System_DataDirPortable"] = "Use the program's own folder (portable)",
            ["System_DataDirHint"] = "Config, logs and the tray icon are stored here. In portable mode everything stays inside the program folder, so the whole folder can be carried on a USB stick. If the app lives under a protected location such as Program Files, prefer %AppData%. Switching copies the existing files over (the old folder is kept) and takes effect immediately.",
            ["System_DataDirCurrent"] = "In effect: {0}",
            ["System_DataDirEnvOverride"] = "In effect: {0} — overridden by the RADIALLAUNCHER_DATA_DIR environment variable, so the choice below has no effect.",
            ["System_DataDirSwitchedTitle"] = "Data folder changed",
            ["System_DataDirSwitchedBody"] = "Copied {0} file(s) to\n{1}",
            ["System_DataDirFailedTitle"] = "Could not change the data folder",
            ["System_DataDirFailedBody"] = "Cannot use \"{0}\":\n{1}",

            // About
            ["About_Version"] = "Version",
            ["About_Log"] = "Log",
            ["About_OpenLog"] = "Open log file",
            ["About_ClearLog"] = "Clear log",
            ["About_RefreshLog"] = "Refresh",
            ["About_Description"] = "Hold Ctrl / Win / the middle mouse button to open a radial launcher at the cursor.",
            ["About_License"] = "GNU GPL v2.0 · free software, no warranty",

            // Tray
            ["Tray_Tooltip"] = "Radial Launcher — right-click for the menu",
            ["Tray_OpenSettings"] = "Open settings",
            ["Tray_Reload"] = "Reload config",
            ["Tray_Test"] = "Test the wheel",
            ["Tray_Log"] = "View log",
            ["Tray_Exit"] = "Exit",

            // Wheel window
            ["Wheel_Release"] = "Release to run",
            ["Wheel_Cancel"] = "Esc to cancel",
            ["Wheel_Empty"] = "No action configured",
            ["Wheel_Profile"] = "App wheel",

            // Color picker
            ["Color_Title"] = "Pick a color",
            ["Color_Hue"] = "Hue",
            ["Color_Saturation"] = "Saturation",
            ["Color_Brightness"] = "Brightness",
            ["Color_Alpha"] = "Alpha",
            ["Color_Hex"] = "Hex",
            ["Color_Eyedropper"] = "Pick from screen",
            ["Color_EyedropperHint"] = "Move the mouse to sample, click to confirm, Esc to cancel",
            ["Color_Presets"] = "Presets",
            ["Color_Preview"] = "Preview",

            // Shortcut picker
            ["Shortcut_Title"] = "Pick a Windows shortcut",
            ["Shortcut_Search"] = "Search name or keys…",
            ["Shortcut_Category"] = "Category",
            ["Shortcut_AllCategories"] = "All categories",
            ["Shortcut_Keys"] = "Keys",
            ["Shortcut_Name"] = "Action",
            ["Shortcut_Select"] = "Use this shortcut",

            // Icon picker
            ["Icon_Title"] = "Pick an icon",
            ["Icon_Hint"] = "Click one, or type any emoji / character below.",
            ["Icon_Custom"] = "Custom character",

            // Messages
            ["Msg_SaveFailed"] = "Failed to save the configuration",
            ["Msg_ActionFailed"] = "Action failed",
            ["Msg_NotConfigured"] = "This slot has no target configured",
            ["Msg_InvalidShortcut"] = "Unrecognized shortcut: {0}",
            ["Msg_FileNotFound"] = "File not found: {0}",
            ["Msg_Done"] = "Done",
            ["Msg_ActionBlockedModifier"] = "\u201c{0}\u201d was not run",
            ["Msg_ActionBlockedModifierHint"] = "A modifier key stayed held, so the run was cancelled to avoid sending a polluted shortcut. Release the keys and try again.",
        };
    }
}
