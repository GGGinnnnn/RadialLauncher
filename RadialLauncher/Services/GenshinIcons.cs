using System;
using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RadialLauncher.Services
{
    /// <summary>
    /// 原神风图标包：从游戏「快捷轮盘」截图里提取出来的白色字形。
    ///
    /// <para>提取方式是「亮度 → alpha」：截图里图标是白字形叠在深色圆底上，
    /// 所以把亮度映射成不透明度、颜色统一刷成白色，就得到可以任意着色的字形，
    /// 而且没有残留的圆底，放到任何背景上都干净。</para>
    ///
    /// <para>分辨率说明：源截图只有 1080p，图标本体约 98 像素；导出时用 4 倍
    /// Lanczos 上采样到 392 像素，缩放到轮盘上的实际尺寸（通常 30~60 像素）
    /// 是绰绰有余的。但这**不是**从游戏资源包里解出来的原始矢量素材，
    /// 真正的 1:1 高清需要解包游戏资源。</para>
    /// </summary>
    public static class GenshinIcons
    {
        /// <summary>图标清单：slug（文件名）→ 中文名 / 英文名。</summary>
        public static readonly IReadOnlyList<(string Slug, string Zh, string En)> Catalog = new[]
        {
            ("shop",         "商城",     "Shop"),
            ("team",         "队伍配置", "Party Setup"),
            ("friends",      "好友",     "Friends"),
            ("archive",      "图鉴",     "Archive"),
            ("char_archive", "角色图鉴", "Character Archive"),
            ("character",    "角色",     "Characters"),
            ("guide",        "提升指南", "Ascension Guide"),
            ("bag",          "背包",     "Inventory"),
            ("quest",        "任务",     "Quests"),
            ("map",          "地图",     "Map"),
            ("event",        "活动",     "Events"),
            ("handbook",     "冒险之证", "Adventurer Handbook"),
            ("wish",         "祈愿",     "Wish"),
            ("battlepass",   "纪行",     "Battle Pass"),
            ("coop",         "多人游戏", "Co-Op Mode"),
            ("camera",       "拍照",     "Camera"),
            ("mail",         "邮件",     "Mail"),
            ("clock",        "时间",     "Time"),
            ("settings",     "设置",     "Settings"),
        };

        private static readonly Dictionary<string, ImageSource> Cache =
            new(StringComparer.OrdinalIgnoreCase);

        private static readonly object Gate = new();

        /// <summary>取图标；失败返回 null（调用方退回 emoji）。</summary>
        public static ImageSource? Load(string slug, double decodeWidth = 128)
        {
            if (string.IsNullOrWhiteSpace(slug)) return null;

            string key = slug.Trim().ToLowerInvariant();
            lock (Gate)
            {
                if (Cache.TryGetValue(key, out var cached)) return cached;
            }

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(
                    $"pack://application:,,,/Resources/Genshin/{key}.png", UriKind.Absolute);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                if (decodeWidth > 0) bitmap.DecodePixelWidth = (int)Math.Max(16, decodeWidth);
                bitmap.EndInit();
                bitmap.Freeze();

                lock (Gate) Cache[key] = bitmap;
                return bitmap;
            }
            catch (Exception ex)
            {
                Logger.Debug($"加载原神图标失败（{slug}）：{ex.Message}");
                return null;
            }
        }

        /// <summary>按中文名或 slug 查图标，供图标选择器使用。</summary>
        public static ImageSource? LoadByName(string? nameOrSlug)
        {
            if (string.IsNullOrWhiteSpace(nameOrSlug)) return null;

            var needle = nameOrSlug.Trim();
            foreach (var (slug, zh, en) in Catalog)
            {
                if (string.Equals(slug, needle, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(zh, needle, StringComparison.Ordinal) ||
                    string.Equals(en, needle, StringComparison.OrdinalIgnoreCase))
                {
                    return Load(slug);
                }
            }

            return Load(needle);
        }
    }
}
