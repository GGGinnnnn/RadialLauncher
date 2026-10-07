using System.Collections.Generic;

namespace RadialLauncher.Models
{
    /// <summary>
    /// 应用全部可持久化配置。保存在 %AppData%\RadialLauncher\config.json
    /// </summary>
    public class AppConfig
    {
        /// <summary>配置结构版本，用于将来做迁移。</summary>
        public int SchemaVersion { get; set; } = 2;

        public TriggerSettings Trigger { get; set; } = new();
        public AppearanceSettings Appearance { get; set; } = new();
        public EnvironmentSettings Environment { get; set; } = new();
        public SystemSettings System { get; set; } = new();

        /// <summary>默认（全局）轮盘的功能项。</summary>
        public List<WheelItem> WheelItems { get; set; } = WheelItem.CreateDefaults();

        /// <summary>应用专属轮盘（浏览器、VS Code 等）。</summary>
        public List<AppProfile> Profiles { get; set; } = AppProfile.CreateDefaults();
    }

    /// <summary>触发方式与计时。</summary>
    public class TriggerSettings
    {
        /// <summary>总开关：关闭后热键完全不再拦截。</summary>
        public bool Enabled { get; set; } = true;

        public bool ByCtrl { get; set; } = true;
        public bool ByWin { get; set; } = false;
        public bool ByMiddleMouse { get; set; } = false;

        /// <summary>需要按住多久（毫秒）才唤出轮盘。</summary>
        public int HoldThresholdMs { get; set; } = 220;

        /// <summary>拦截 Win 键，避免唤出轮盘时弹出开始菜单。</summary>
        public bool SuppressWinKey { get; set; } = true;

        /// <summary>拦截鼠标中键，避免误触发中键滚动/粘贴。</summary>
        public bool SuppressMiddleClick { get; set; } = true;

        /// <summary>按住触发键期间按下其他键时取消唤出（防止影响 Ctrl+C 等组合键）。</summary>
        public bool CancelOnOtherKey { get; set; } = true;

        /// <summary>唤出轮盘时播放淡入缩放动画。</summary>
        public bool EnableAnimation { get; set; } = true;
    }

    /// <summary>外观。</summary>
    public class AppearanceSettings
    {
        /// <summary>轮盘方向数：4 或 8。</summary>
        public int ItemCount { get; set; } = 8;

        public string BackgroundColor { get; set; } = "#E61A1A2E";
        public string BorderColor { get; set; } = "#FF4A9EFF";
        public string ItemColor { get; set; } = "#E62A2A3A";
        public string ItemSelectedColor { get; set; } = "#FFFF6B35";
        public string TextColor { get; set; } = "#FFFFFFFF";

        /// <summary>轮盘整体缩放（0.6 ~ 1.6）。</summary>
        public double Scale { get; set; } = 1.0;

        /// <summary>选项按钮直径（DIP）。</summary>
        public double ItemSize { get; set; } = 74;

        /// <summary>中心死区半径：鼠标离圆心小于该值时不选中任何项。</summary>
        public double DeadZone { get; set; } = 34;

        /// <summary>是否显示外发光。</summary>
        public bool ShowGlow { get; set; } = true;

        /// <summary>轮盘整体不透明度（0.3 ~ 1.0）。</summary>
        public double Opacity { get; set; } = 1.0;

        /// <summary>选中后是否自动执行（松开触发键时）。关闭则必须点击。</summary>
        public bool ExecuteOnRelease { get; set; } = true;
    }

    /// <summary>触发环境（白/黑名单）与应用专属轮盘开关。</summary>
    public class EnvironmentSettings
    {
        /// <summary>true = 白名单模式（仅列表内进程可触发）。</summary>
        public bool UseWhitelist { get; set; } = false;

        public List<string> WhitelistProcesses { get; set; } = new();
        public List<string> BlacklistProcesses { get; set; } = new();

        /// <summary>是否启用应用专属轮盘。</summary>
        public bool ProfilesEnabled { get; set; } = true;

        /// <summary>在全屏程序（游戏）中自动禁用，避免干扰。</summary>
        public bool SkipFullscreenApps { get; set; } = true;
    }

    /// <summary>系统设置。</summary>
    public class SystemSettings
    {
        /// <summary>界面语言：zh-CN / en-US。</summary>
        public string Language { get; set; } = "zh-CN";

        /// <summary>
        /// 界面字体族名称（可写逗号分隔的回退链）。
        /// 默认微软雅黑，找不到时由 WPF 自动回退到列表后面的字体。
        /// </summary>
        public string UiFont { get; set; } = "Microsoft YaHei UI";

        public bool AutoStart { get; set; } = false;

        /// <summary>以管理员身份运行（启动时若未提权会尝试重新以管理员启动）。</summary>
        public bool RunAsAdmin { get; set; } = false;

        /// <summary>启动时是否显示托盘气泡提示。</summary>
        public bool ShowTrayTip { get; set; } = false;

        public string GitHubUrl { get; set; } = "https://github.com/GGGinnnnn/RadialLauncher";
    }

    /// <summary>一个轮盘选项。</summary>
    public class WheelItem
    {
        public string Name { get; set; } = "";

        /// <summary>emoji 或单个字符，作为图标显示。</summary>
        public string Icon { get; set; } = "●";

        /// <summary>图标图片路径；设置后优先于 emoji。</summary>
        public string IconPath { get; set; } = "";

        /// <summary>
        /// LaunchApp / OpenFolder / OpenUrl / Shortcut / Command / Script / Builtin
        /// </summary>
        public string ActionType { get; set; } = "Shortcut";

        /// <summary>主参数：路径 / 快捷键序列 / 命令行 / 内置功能名。</summary>
        public string ActionValue { get; set; } = "";

        /// <summary>命令行附加参数。</summary>
        public string Arguments { get; set; } = "";

        /// <summary>工作目录（对 Command / Script / LaunchApp 生效）。</summary>
        public string WorkingDirectory { get; set; } = "";

        /// <summary>以管理员身份执行。</summary>
        public bool RunAsAdmin { get; set; } = false;

        /// <summary>启动后是否隐藏窗口（Command 用）。</summary>
        public bool HiddenWindow { get; set; } = false;

        public WheelItem Clone() => (WheelItem)MemberwiseClone();

        public static List<WheelItem> CreateDefaults() => new()
        {
            new WheelItem { Name = "QQ",      Icon = "💬", ActionType = "LaunchApp",  ActionValue = "" },
            new WheelItem { Name = "微信",    Icon = "💚", ActionType = "LaunchApp",  ActionValue = "" },
            new WheelItem { Name = "原神",    Icon = "⚔",  ActionType = "LaunchApp",  ActionValue = "" },
            new WheelItem { Name = "下载",    Icon = "📁", ActionType = "OpenFolder", ActionValue = "" },
            new WheelItem { Name = "显示桌面", Icon = "🖥", ActionType = "Shortcut",   ActionValue = "Win+D" },
            new WheelItem { Name = "锁屏",    Icon = "🔒", ActionType = "Shortcut",   ActionValue = "Win+L" },
            new WheelItem { Name = "剪贴板",  Icon = "📋", ActionType = "Shortcut",   ActionValue = "Win+V" },
            new WheelItem { Name = "任务管理", Icon = "📊", ActionType = "Shortcut",   ActionValue = "Ctrl+Shift+Esc" },
        };
    }

    /// <summary>应用专属轮盘配置。</summary>
    public class AppProfile
    {
        public string Name { get; set; } = "";

        /// <summary>匹配的进程名（不含 .exe，忽略大小写）。</summary>
        public List<string> ProcessNames { get; set; } = new();

        /// <summary>匹配的窗口标题关键字（可选，留空则不限制）。</summary>
        public List<string> TitleKeywords { get; set; } = new();

        public bool Enabled { get; set; } = true;

        /// <summary>true = 使用全局轮盘内容，只覆盖方向数。</summary>
        public bool UseGlobalItems { get; set; } = false;

        /// <summary>方向数覆盖：0 表示跟随全局。</summary>
        public int ItemCountOverride { get; set; } = 0;

        public List<WheelItem> Items { get; set; } = new();

        /// <summary>图标（emoji）。</summary>
        public string Icon { get; set; } = "🧩";

        public AppProfile Clone() => new()
        {
            Name = Name,
            ProcessNames = new List<string>(ProcessNames),
            TitleKeywords = new List<string>(TitleKeywords),
            Enabled = Enabled,
            UseGlobalItems = UseGlobalItems,
            ItemCountOverride = ItemCountOverride,
            Icon = Icon,
            Items = Items.ConvertAll(i => i.Clone()),
        };

        public override string ToString()
        {
            var icon = string.IsNullOrWhiteSpace(Icon) ? "🧩" : Icon;
            var name = string.IsNullOrWhiteSpace(Name) ? "(未命名)" : Name;
            return $"{icon}  {name}";
        }

        /// <summary>把当前窗口信息加入此配置。</summary>
        public bool Matches(string processName, string windowTitle)
        {
            if (!Enabled || ProcessNames.Count == 0) return false;

            bool procOk = false;
            foreach (var p in ProcessNames)
            {
                if (string.Equals(p, processName, System.StringComparison.OrdinalIgnoreCase))
                {
                    procOk = true;
                    break;
                }
            }
            if (!procOk) return false;

            if (TitleKeywords.Count == 0) return true;

            foreach (var k in TitleKeywords)
            {
                if (string.IsNullOrWhiteSpace(k)) continue;
                if (windowTitle.Contains(k, System.StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public static List<AppProfile> CreateDefaults() => new()
        {
            new AppProfile
            {
                Name = "浏览器",
                Icon = "🌐",
                ProcessNames = new() { "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "360se", "360chrome" },
                Items = new()
                {
                    new WheelItem { Name = "后退",   Icon = "⬅", ActionType = "Shortcut", ActionValue = "Alt+Left" },
                    new WheelItem { Name = "前进",   Icon = "➡", ActionType = "Shortcut", ActionValue = "Alt+Right" },
                    new WheelItem { Name = "新标签页", Icon = "➕", ActionType = "Shortcut", ActionValue = "Ctrl+T" },
                    new WheelItem { Name = "关闭标签", Icon = "✖", ActionType = "Shortcut", ActionValue = "Ctrl+W" },
                    new WheelItem { Name = "恢复标签", Icon = "♻", ActionType = "Shortcut", ActionValue = "Ctrl+Shift+T" },
                    new WheelItem { Name = "刷新",   Icon = "🔄", ActionType = "Shortcut", ActionValue = "F5" },
                    new WheelItem { Name = "地址栏", Icon = "🔗", ActionType = "Shortcut", ActionValue = "Ctrl+L" },
                    new WheelItem { Name = "无痕窗口", Icon = "🕶", ActionType = "Shortcut", ActionValue = "Ctrl+Shift+N" },
                },
            },
            new AppProfile
            {
                Name = "VS Code",
                Icon = "🧑‍💻",
                ProcessNames = new() { "code", "code - insiders", "cursor", "vscodium" },
                Items = new()
                {
                    new WheelItem { Name = "终端",   Icon = "⌨", ActionType = "Shortcut", ActionValue = "Ctrl+`" },
                    new WheelItem { Name = "命令面板", Icon = "🎛", ActionType = "Shortcut", ActionValue = "Ctrl+Shift+P" },
                    new WheelItem { Name = "查找",   Icon = "🔍", ActionType = "Shortcut", ActionValue = "Ctrl+F" },
                    new WheelItem { Name = "全局搜索", Icon = "🔎", ActionType = "Shortcut", ActionValue = "Ctrl+Shift+F" },
                    new WheelItem { Name = "文件",   Icon = "📄", ActionType = "Shortcut", ActionValue = "Ctrl+Shift+E" },
                    new WheelItem { Name = "源代码管理", Icon = "🌿", ActionType = "Shortcut", ActionValue = "Ctrl+Shift+G" },
                    new WheelItem { Name = "调试",   Icon = "🐞", ActionType = "Shortcut", ActionValue = "Ctrl+Shift+D" },
                    new WheelItem { Name = "设置",   Icon = "⚙", ActionType = "Shortcut", ActionValue = "Ctrl+," },
                },
            },
            new AppProfile
            {
                Name = "资源管理器",
                Icon = "🗂",
                ProcessNames = new() { "explorer" },
                Items = new()
                {
                    new WheelItem { Name = "新窗口", Icon = "🪟", ActionType = "Shortcut", ActionValue = "Ctrl+N" },
                    new WheelItem { Name = "地址栏", Icon = "🔗", ActionType = "Shortcut", ActionValue = "Alt+D" },
                    new WheelItem { Name = "后退",   Icon = "⬅", ActionType = "Shortcut", ActionValue = "Alt+Left" },
                    new WheelItem { Name = "前进",   Icon = "➡", ActionType = "Shortcut", ActionValue = "Alt+Right" },
                    new WheelItem { Name = "上级",   Icon = "⬆", ActionType = "Shortcut", ActionValue = "Alt+Up" },
                    new WheelItem { Name = "搜索",   Icon = "🔍", ActionType = "Shortcut", ActionValue = "Ctrl+E" },
                    new WheelItem { Name = "删除",   Icon = "🗑", ActionType = "Shortcut", ActionValue = "Delete" },
                    new WheelItem { Name = "重命名", Icon = "✏", ActionType = "Shortcut", ActionValue = "F2" },
                },
            },
        };
    }
}
