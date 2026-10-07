using System;
using System.Collections.Generic;
using System.Linq;

namespace RadialLauncher.Services
{
    /// <summary>
    /// Windows 快捷键目录。数据在同名的 ShortcutCatalog.Data.cs 里。
    /// </summary>
    public static partial class ShortcutCatalog
    {
        public static IReadOnlyList<ShortcutDefinition> Items => All;

        /// <summary>按分类分组，顺序与数据文件一致。</summary>
        public static List<IGrouping<string, ShortcutDefinition>> Grouped(bool english)
        {
            return All
                .GroupBy(s => english ? s.CategoryEn : s.CategoryZh)
                .ToList();
        }

        /// <summary>分类列表（按数据文件出现顺序）。</summary>
        public static List<string> Categories(bool english)
        {
            var result = new List<string>();
            foreach (var s in All)
            {
                var cat = english ? s.CategoryEn : s.CategoryZh;
                if (!result.Contains(cat)) result.Add(cat);
            }
            return result;
        }

        public static ShortcutDefinition? ById(string id)
            => All.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

        /// <summary>按按键序列查找（用于把已有配置回显成友好名称）。</summary>
        public static ShortcutDefinition? ByKeys(string keys)
        {
            if (string.IsNullOrWhiteSpace(keys)) return null;
            var norm = Normalize(keys);
            return All.FirstOrDefault(s => Normalize(s.Keys) == norm);
        }

        public static string DisplayName(ShortcutDefinition s, bool english)
            => english ? s.NameEn : s.NameZh;

        public static string CategoryName(ShortcutDefinition s, bool english)
            => english ? s.CategoryEn : s.CategoryZh;

        /// <summary>在全部条目里按名称/按键/分类搜索。</summary>
        public static List<ShortcutDefinition> Search(string? query, bool english)
        {
            if (string.IsNullOrWhiteSpace(query)) return All.ToList();

            var q = query.Trim();
            return All.Where(s =>
                    Contains(s.NameZh, q) ||
                    Contains(s.NameEn, q) ||
                    Contains(s.Keys, q) ||
                    Contains(s.CategoryZh, q) ||
                    Contains(s.CategoryEn, q) ||
                    Contains(s.Id, q))
                .ToList();
        }

        private static bool Contains(string? source, string value)
            => !string.IsNullOrEmpty(source) && source.Contains(value, StringComparison.OrdinalIgnoreCase);

        /// <summary>把按键文本规范化为小写、无空格的比较形式。</summary>
        public static string Normalize(string keys)
        {
            if (string.IsNullOrEmpty(keys)) return "";
            var parts = keys.Split('+', StringSplitOptions.RemoveEmptyEntries)
                            .Select(p => p.Trim().ToLowerInvariant())
                            .ToList();
            parts.Sort(StringComparer.Ordinal);
            return string.Join("+", parts);
        }

        /// <summary>校验一条快捷键文本能否被合成器解析。</summary>
        public static bool IsValidKeys(string? keys, out string error)
            => KeySequence.TryParse(keys, out _, out error);
    }
}
