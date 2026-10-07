using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Media;

namespace RadialLauncher.Services
{
    /// <summary>一个可选字体。</summary>
    public sealed class FontOption
    {
        public string Family { get; init; } = "";

        /// <summary>中文显示名。</summary>
        public string DisplayZh { get; init; } = "";

        /// <summary>英文显示名。</summary>
        public string DisplayEn { get; init; } = "";

        /// <summary>是否装在这台机器上。</summary>
        public bool Installed { get; init; }

        /// <summary>备注（例如“游戏自带字体，需自行安装”）。</summary>
        public string NoteZh { get; init; } = "";
        public string NoteEn { get; init; } = "";

        public string Display(bool english)
        {
            var name = english ? DisplayEn : DisplayZh;
            if (string.IsNullOrWhiteSpace(name)) name = Family;
            return Installed ? name : $"{name} · " + Loc.Pick("未安装", "not installed");
        }
    }

    /// <summary>
    /// 界面字体目录：推荐候选 + 本机已安装字体的实时探测。
    /// 找不到的字体不会报错——WPF 会沿 FontFamily 的回退链继续找，
    /// 所以即便用户选了没装的原神 / Minecraft 字体，界面也不会变成方框。
    /// </summary>
    public static class FontCatalog
    {
        /// <summary>界面字体的最终回退链尾部，保证永远有字可用。</summary>
        public const string FallbackChain = "Microsoft YaHei UI, Segoe UI, Arial";

        private static HashSet<string>? _installed;
        private static List<string>? _installedSorted;

        /// <summary>本机已安装字体族名（小写，便于比较）。</summary>
        private static HashSet<string> InstalledSet
        {
            get
            {
                if (_installed != null) return _installed;

                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (var family in Fonts.SystemFontFamilies)
                    {
                        // Source 通常是英文名；FamilyNames 里还有各语言的本地化名
                        if (!string.IsNullOrWhiteSpace(family.Source)) set.Add(family.Source);

                        try
                        {
                            foreach (var pair in family.FamilyNames)
                            {
                                if (!string.IsNullOrWhiteSpace(pair.Value)) set.Add(pair.Value);
                            }
                        }
                        catch
                        {
                            // 个别字体读不到本地化名，忽略
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn("枚举系统字体失败：" + ex.Message);
                }

                _installed = set;
                return set;
            }
        }

        /// <summary>本机已安装字体，按名称排序。</summary>
        public static List<string> InstalledFamilies
        {
            get
            {
                if (_installedSorted != null) return _installedSorted;

                try
                {
                    _installedSorted = Fonts.SystemFontFamilies
                        .Select(f => f.Source)
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(s => s, StringComparer.CurrentCultureIgnoreCase)
                        .ToList();
                }
                catch
                {
                    _installedSorted = new List<string>();
                }

                return _installedSorted;
            }
        }

        public static bool IsInstalled(string family)
        {
            if (string.IsNullOrWhiteSpace(family)) return false;
            return InstalledSet.Contains(family.Trim());
        }

        /// <summary>用户在系统里新装了字体后，清掉缓存重新探测。</summary>
        public static void InvalidateCache()
        {
            _installed = null;
            _installedSorted = null;
            Logger.Info("字体缓存已清空，下次读取将重新枚举系统字体");
        }

        /// <summary>推荐字体（含游戏字体）。Installed 会实时探测。</summary>
        public static List<FontOption> Recommendations()
        {
            var raw = new (string Family, string Zh, string En, string NoteZh, string NoteEn)[]
            {
                ("Microsoft YaHei UI", "微软雅黑 UI", "Microsoft YaHei UI", "", ""),
                ("Microsoft YaHei", "微软雅黑", "Microsoft YaHei", "", ""),
                ("Segoe UI Variable Display", "Segoe UI Variable", "Segoe UI Variable", "", ""),
                ("Segoe UI", "Segoe UI（系统默认）", "Segoe UI (system default)", "", ""),
                ("Arial", "Arial", "Arial", "", ""),
                ("Tahoma", "Tahoma", "Tahoma", "", ""),
                ("Calibri", "Calibri", "Calibri", "", ""),
                ("Verdana", "Verdana", "Verdana", "", ""),
                ("思源黑体 CN", "思源黑体", "Source Han Sans", "需自行安装", "install separately"),
                ("HarmonyOS Sans SC", "鸿蒙 Sans", "HarmonyOS Sans", "需自行安装", "install separately"),
                ("Alibaba PuHuiTi", "阿里巴巴普惠体", "Alibaba PuHuiTi", "需自行安装", "install separately"),
                ("HYWenHei-85W", "原神（汉仪文黑）", "Genshin (HYWenHei)", "原神自带字体，需自行安装", "ships with Genshin, install separately"),
                ("汉仪文黑-85W", "原神（汉仪文黑 · 中文名）", "Genshin (HYWenHei CN)", "原神自带字体，需自行安装", "ships with Genshin, install separately"),
                ("Minecraft", "Minecraft", "Minecraft", "游戏自带字体，需自行安装", "ships with the game, install separately"),
                ("Minecraft Ten", "Minecraft Ten", "Minecraft Ten", "游戏自带字体，需自行安装", "ships with the game, install separately"),
                ("Zpix", "最像素", "Zpix pixel", "像素字体，需自行安装", "pixel font, install separately"),
                ("Consolas", "Consolas（等宽）", "Consolas (monospace)", "", ""),
            };

            return raw.Select(r => new FontOption
            {
                Family = r.Family,
                DisplayZh = r.Zh,
                DisplayEn = r.En,
                NoteZh = r.NoteZh,
                NoteEn = r.NoteEn,
                Installed = IsInstalled(r.Family),
            }).ToList();
        }

        /// <summary>
        /// 把用户选的字体拼成带回退链的 FontFamily 字符串。
        /// 用户选的字体永远排在第一个 —— 否则选了微软雅黑也会被
        /// 回退链里的 Segoe UI 抢走，等于没选。
        /// 例如 "Microsoft YaHei UI" → "Microsoft YaHei UI, Segoe UI, Arial"。
        /// </summary>
        public static string BuildFamilyChain(string? chosen)
        {
            var parts = new List<string>();

            void Add(string? candidate)
            {
                var name = (candidate ?? "").Trim();
                if (name.Length == 0) return;
                if (parts.Exists(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase))) return;
                parts.Add(name);
            }

            // 用户写的可能是逗号分隔的一串，逐个收进来
            foreach (var part in (chosen ?? "").Split(',')) Add(part);

            // 再接上兜底链
            foreach (var part in FallbackChain.Split(',')) Add(part);

            return parts.Count > 0 ? string.Join(", ", parts) : FallbackChain;
        }

        /// <summary>世界上有没有这个字体不重要，能显示出来就行——用于界面预览。</summary>
        public static FontFamily Resolve(string? chosen)
        {
            try
            {
                return new FontFamily(BuildFamilyChain(chosen));
            }
            catch (Exception ex)
            {
                Logger.Warn($"字体 {chosen} 解析失败，回退默认：{ex.Message}");
                return new FontFamily(FallbackChain);
            }
        }

        /// <summary>当前字体实际会用到哪一个（用于告诉用户真实生效的字体）。</summary>
        public static string DescribeEffective(string? chosen)
        {
            if (!string.IsNullOrWhiteSpace(chosen) && IsInstalled(chosen.Trim()))
                return chosen.Trim();

            foreach (var part in FallbackChain.Split(','))
            {
                var candidate = part.Trim();
                if (string.IsNullOrEmpty(candidate)) continue;
                if (IsInstalled(candidate)) return candidate;
            }

            return CultureInfo.CurrentUICulture.Name;
        }
    }
}
