using System;
using System.Collections.Generic;

namespace RadialLauncher.Services
{
    /// <summary>
    /// 自定义触发键的名字表与校验 —— 纯逻辑，不碰 Win32、不碰 WPF，因此可以单独测。
    ///
    /// <para>配置里保存的是**按键名**（如 "F13"、"CapsLock"、"Shift"）而不是裸虚拟键码：
    /// 名字既能让人直接看懂 config.json，也便于将来换键名表时做迁移。真正喂给钩子的
    /// 虚拟键码由 <see cref="ToVks"/> 在构造设置快照时一次性解析出来。</para>
    ///
    /// <para>键名解析复用 <see cref="KeySequence.TryResolveKey"/>，与“Windows 快捷键”
    /// 功能共用同一张表 —— 否则同一个键在两处叫不同名字，用户会莫名其妙。</para>
    /// </summary>
    public static class CustomTriggerKeys
    {
        /// <summary>自定义触发键最多允许几个（配置坏掉时不至于拖慢钩子里的查表）。</summary>
        public const int MaxCount = 8;

        /// <summary>
        /// 保留键名：这些键已经有专属处理（Ctrl/Win/Alt 是预设触发键，Esc 是取消键），
        /// 再加进自定义列表只会是死配置，还会让用户以为设置没生效，所以直接拒绝。
        /// </summary>
        private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
        {
            "Ctrl", "Control",
            "Win", "Windows", "Meta",
            "Alt", "Menu",
            "Esc", "Escape",
        };

        /// <summary>设置面板下拉框里的推荐项（大多单独按下去没有系统行为，适合长按触发）。</summary>
        public static readonly string[] Suggestions =
        [
            "Shift",
            "CapsLock", "NumLock", "ScrollLock",
            "Apps", "Pause", "PrintScreen",
            "Tab", "Space", "Insert", "Delete",
            "Home", "End", "PageUp", "PageDown",
            "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8",
            "F9", "F10", "F11", "F12", "F13", "F14", "F15", "F16",
            "F17", "F18", "F19", "F20", "F21", "F22", "F23", "F24",
            "VolumeMute", "VolumeDown", "VolumeUp",
            "NextTrack", "PrevTrack", "StopMedia", "PlayPause",
        ];

        /// <summary>名字是否合法且允许作为自定义触发键。</summary>
        public static bool IsAllowed(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            var trimmed = name.Trim();
            if (Reserved.Contains(trimmed)) return false;
            return KeySequence.TryResolveKey(trimmed, out _);
        }

        /// <summary>
        /// 把配置里的自定义触发键收敛成干净列表：去空白、去保留键、去无法识别的名字、
        /// 按虚拟键码去重，并截断到 <see cref="MaxCount"/> 个。顺序保持不变。
        /// </summary>
        public static List<string> Normalize(IEnumerable<string>? names)
        {
            var result = new List<string>();
            if (names == null) return result;

            var seenVks = new HashSet<int>();

            foreach (var raw in names)
            {
                if (result.Count >= MaxCount) break;
                if (string.IsNullOrWhiteSpace(raw)) continue;

                var name = raw.Trim();
                if (!IsAllowed(name)) continue;
                if (!KeySequence.TryResolveKey(name, out int vk)) continue;
                if (!seenVks.Add(vk)) continue;

                result.Add(name);
            }

            return result;
        }

        /// <summary>
        /// 泛修饰键 → 左右两侧键码的展开表。
        ///
        /// <para>名字表里 "Shift" 解析成泛键码 0x10，但低级键盘钩子报告的是左右分离的
        /// VK_LSHIFT(0xA0) / VK_RSHIFT(0xA1) —— 不展开的话，用户把 Shift 设为触发键后
        /// 按左右 Shift 都不会命中。Ctrl / Alt / Win 有各自的预设分支负责同时匹配泛码与
        /// 左右码，所以这里只需要展开 Shift。</para>
        /// </summary>
        private static int[] ExpandModifier(int vk) => vk switch
        {
            0x10 => [0x10, 0xA0, 0xA1],   // Shift → 左/右 Shift
            _ => [vk],
        };

        /// <summary>把名字列表解析成去重后的虚拟键码序列（喂给钩子用；已展开左右键码）。</summary>
        public static int[] ToVks(IEnumerable<string>? names)
        {
            // 先按“名字”收敛（含 MaxCount 截断），再展开成键码：
            // 截断数的是用户配置的键数，不是展开后的左右码数，
            // 否则 Shift 一个键就会吃掉 3 个名额。
            var normalized = Normalize(names);

            var vks = new List<int>();
            var seen = new HashSet<int>();
            foreach (var name in normalized)
            {
                if (!KeySequence.TryResolveKey(name, out int vk)) continue;
                foreach (var expanded in ExpandModifier(vk))
                {
                    if (seen.Add(expanded)) vks.Add(expanded);
                }
            }

            return vks.ToArray();
        }

        /// <summary>名字列表的可读形式，用于状态栏与日志（"F13 + CapsLock"）。</summary>
        public static string Describe(IEnumerable<string>? names)
        {
            var list = Normalize(names);
            return list.Count == 0 ? "" : string.Join(" + ", list);
        }
    }
}
