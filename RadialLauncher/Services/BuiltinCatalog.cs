using System.Collections.Generic;
using System.Linq;

namespace RadialLauncher.Services
{
    public sealed record BuiltinDefinition(string Id, string NameZh, string NameEn, string Icon, string CategoryZh, string CategoryEn);

    /// <summary>内置功能：不需要用户填写路径、由程序自己完成的动作。</summary>
    public static class BuiltinCatalog
    {
        public static IReadOnlyList<BuiltinDefinition> Items { get; } = new List<BuiltinDefinition>
        {
            // 系统
            new("LockScreen",     "锁定屏幕",       "Lock screen",          "🔒", "系统", "System"),
            new("Sleep",          "睡眠",           "Sleep",                "🌙", "系统", "System"),
            new("Hibernate",      "休眠",           "Hibernate",            "🛌", "系统", "System"),
            new("Shutdown",       "关机",           "Shut down",            "⏻",  "系统", "System"),
            new("Restart",        "重启",           "Restart",              "🔄", "系统", "System"),
            new("LogOff",         "注销",           "Sign out",             "🚪", "系统", "System"),
            new("TaskManager",    "任务管理器",     "Task Manager",         "📊", "系统", "System"),
            new("OpenSettings",   "Windows 设置",   "Windows Settings",     "⚙",  "系统", "System"),
            new("EmptyRecycleBin","清空回收站",     "Empty Recycle Bin",    "🗑", "系统", "System"),
            new("OpenDataFolder", "打开配置目录",   "Open config folder",   "📂", "系统", "System"),

            // 桌面与窗口
            new("ShowDesktop",    "显示桌面",       "Show desktop",         "🖥", "桌面", "Desktop"),
            new("OpenExplorer",   "打开文件资源管理器", "Open File Explorer","🗂", "桌面", "Desktop"),
            new("OpenTerminal",   "打开终端",       "Open terminal",        "⌨",  "桌面", "Desktop"),
            new("OpenDownloads",  "打开下载文件夹", "Open Downloads",       "⬇",  "桌面", "Desktop"),
            new("OpenDocuments",  "打开文档文件夹", "Open Documents",       "📄", "桌面", "Desktop"),
            new("OpenDesktopDir", "打开桌面文件夹", "Open Desktop folder",  "🖼", "桌面", "Desktop"),

            // 媒体
            new("PlayPause",      "播放 / 暂停",    "Play / Pause",         "⏯", "媒体", "Media"),
            new("NextTrack",      "下一曲",         "Next track",           "⏭", "媒体", "Media"),
            new("PrevTrack",      "上一曲",         "Previous track",       "⏮", "媒体", "Media"),
            new("StopMedia",      "停止播放",       "Stop playback",        "⏹", "媒体", "Media"),
            new("VolumeUp",       "音量 +",         "Volume up",            "🔊", "媒体", "Media"),
            new("VolumeDown",     "音量 −",         "Volume down",          "🔉", "媒体", "Media"),
            new("Mute",           "静音",           "Mute",                 "🔇", "媒体", "Media"),

            // 其他
            new("Clipboard",      "剪贴板历史",     "Clipboard history",    "📋", "其他", "Other"),
            new("Screenshot",     "截图",           "Screenshot",           "✂",  "其他", "Other"),
            new("EmojiPanel",     "表情符号面板",   "Emoji panel",          "😀", "其他", "Other"),
            new("Search",         "搜索",           "Search",               "🔍", "其他", "Other"),
            new("RunDialog",      "运行对话框",     "Run dialog",           "▶",  "其他", "Other"),
        };

        public static BuiltinDefinition? ById(string id)
            => Items.FirstOrDefault(i => i.Id == id);

        public static string DisplayName(string id)
        {
            var def = ById(id);
            if (def == null) return string.IsNullOrEmpty(id) ? Loc.T("Common_None") : id;
            return Loc.Instance.IsEnglish ? def.NameEn : def.NameZh;
        }

        public static List<string> Categories()
        {
            var list = new List<string>();
            foreach (var item in Items)
            {
                var cat = Loc.Instance.IsEnglish ? item.CategoryEn : item.CategoryZh;
                if (!list.Contains(cat)) list.Add(cat);
            }
            return list;
        }
    }
}
