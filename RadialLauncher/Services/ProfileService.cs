using System.Collections.Generic;
using RadialLauncher.Models;

namespace RadialLauncher.Services
{
    /// <summary>当前应当显示哪一个轮盘。</summary>
    public sealed class ActiveWheel
    {
        public AppProfile? Profile { get; init; }
        public List<WheelItem> Items { get; init; } = new();
        public int ItemCount { get; init; } = 8;

        /// <summary>是否命中应用专属配置。</summary>
        public bool IsProfile => Profile != null;

        public string Title => Profile?.Name ?? "";
        public string Icon => Profile?.Icon ?? "";
    }

    /// <summary>根据前台窗口决定使用全局轮盘还是应用专属轮盘。</summary>
    public static class ProfileService
    {
        public static ActiveWheel Resolve(AppConfig config, ForegroundWindowInfo info)
        {
            var env = config.Environment;
            var appearance = config.Appearance;

            if (env.ProfilesEnabled)
            {
                foreach (var profile in config.Profiles)
                {
                    if (profile == null) continue;
                    if (!profile.Matches(info.ProcessName, info.Title)) continue;

                    // 命中：方向数可以用配置覆盖，也可以用全局
                    int count = profile.ItemCountOverride is 4 or 8
                        ? profile.ItemCountOverride
                        : appearance.ItemCount;

                    var items = profile.UseGlobalItems
                        ? config.WheelItems
                        : profile.Items;

                    // 兜底：专属配置还没填任何功能项时退回全局轮盘，
                    // 否则用户在那个应用里会唤出一个空轮盘。
                    if (items == null || items.Count == 0)
                    {
                        Logger.Warn($"应用专属配置「{profile.Name}」没有任何功能项，暂时使用全局轮盘");
                        items = config.WheelItems;
                    }

                    Logger.Debug($"命中应用专属配置「{profile.Name}」（进程 {info.ProcessName}）");

                    return new ActiveWheel
                    {
                        Profile = profile,
                        Items = items,
                        ItemCount = count,
                    };
                }
            }

            return new ActiveWheel
            {
                Profile = null,
                Items = config.WheelItems,
                ItemCount = appearance.ItemCount,
            };
        }

        /// <summary>找出与某个进程名匹配的配置（设置面板用）。</summary>
        public static AppProfile? FindByProcess(AppConfig config, string processName)
        {
            foreach (var profile in config.Profiles)
            {
                foreach (var name in profile.ProcessNames)
                {
                    if (string.Equals(name, processName, System.StringComparison.OrdinalIgnoreCase))
                        return profile;
                }
            }
            return null;
        }
    }
}
