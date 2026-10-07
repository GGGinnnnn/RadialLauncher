using System;
using System.Collections.Generic;
using System.Text;

namespace RadialLauncher.Services
{
    /// <summary>
    /// 把 "Ctrl+Shift+Esc" 这类文本解析成虚拟键序列，并合成为真实按键。
    /// </summary>
    public static class KeySequence
    {
        public const int VK_CTRL = 0x11;
        public const int VK_SHIFT = 0x10;
        public const int VK_ALT = 0x12;
        public const int VK_WIN = 0x5B;

        private static readonly Dictionary<string, int> Map =
            new(StringComparer.OrdinalIgnoreCase)
            {
                // 修饰键
                ["Ctrl"] = VK_CTRL, ["Control"] = VK_CTRL,
                ["Shift"] = VK_SHIFT,
                ["Alt"] = VK_ALT, ["Menu"] = VK_ALT,
                ["Win"] = VK_WIN, ["Windows"] = VK_WIN, ["Meta"] = VK_WIN,

                // 功能键
                ["Esc"] = 0x1B, ["Escape"] = 0x1B,
                ["Tab"] = 0x09,
                ["Enter"] = 0x0D, ["Return"] = 0x0D,
                ["Space"] = 0x20, ["Spacebar"] = 0x20,
                ["Backspace"] = 0x08, ["Back"] = 0x08,
                ["Delete"] = 0x2E, ["Del"] = 0x2E,
                ["Insert"] = 0x2D, ["Ins"] = 0x2D,
                ["Home"] = 0x24, ["End"] = 0x23,
                ["PageUp"] = 0x21, ["PgUp"] = 0x21,
                ["PageDown"] = 0x22, ["PgDn"] = 0x22,
                ["Up"] = 0x26, ["Down"] = 0x28, ["Left"] = 0x25, ["Right"] = 0x27,
                ["PrintScreen"] = 0x2C, ["PrtSc"] = 0x2C, ["PrtScn"] = 0x2C,
                ["Pause"] = 0x13, ["Break"] = 0x13,
                ["CapsLock"] = 0x14, ["NumLock"] = 0x90, ["ScrollLock"] = 0x91,
                ["Apps"] = 0x5D, ["ContextMenu"] = 0x5D,

                // 媒体 / 音量键
                ["VolumeUp"] = 0xAF, ["VolumeDown"] = 0xAE, ["VolumeMute"] = 0xAD, ["Mute"] = 0xAD,
                ["PlayPause"] = 0xB3, ["MediaPlayPause"] = 0xB3,
                ["NextTrack"] = 0xB0, ["MediaNextTrack"] = 0xB0,
                ["PrevTrack"] = 0xB1, ["MediaPrevTrack"] = 0xB1,
                ["StopMedia"] = 0xB2, ["MediaStop"] = 0xB2,

                // 符号键（OEM）
                ["`"] = 0xC0, ["~"] = 0xC0,
                ["-"] = 0xBD, ["_"] = 0xBD,
                ["="] = 0xBB, ["+"] = 0xBB,
                ["["] = 0xDB, ["{"] = 0xDB,
                ["]"] = 0xDD, ["}"] = 0xDD,
                ["\\"] = 0xDC, ["|"] = 0xDC,
                [";"] = 0xBA, [":"] = 0xBA,
                ["'"] = 0xDE, ["\""] = 0xDE,
                [","] = 0xBC, ["<"] = 0xBC,
                ["."] = 0xBE, [">"] = 0xBE,
                ["/"] = 0xBF, ["?"] = 0xBF,
            };

        /// <summary>需要扩展键标志的虚拟键。</summary>
        private static readonly HashSet<int> ExtendedKeys = new()
        {
            0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, // PgUp/PgDn/End/Home/方向键
            0x2D, 0x2E,                                     // Insert/Delete
            0x2C,                                           // PrintScreen
            0x5D,                                           // Apps
            0x90,                                           // NumLock
            0x5B, 0x5C,                                     // Win
            0xAD, 0xAE, 0xAF,                               // 音量
            0xB0, 0xB1, 0xB2, 0xB3,                         // 媒体控制
        };

        /// <summary>把快捷键文本解析为虚拟键列表（修饰键在前，主键在后）。</summary>
        public static bool TryParse(string? text, out List<int> keys, out string error)
        {
            keys = new List<int>();
            error = "";

            if (string.IsNullOrWhiteSpace(text))
            {
                error = "快捷键为空";
                return false;
            }

            var seen = new HashSet<int>();
            var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries);

            foreach (var raw in parts)
            {
                var token = raw.Trim();
                if (token.Length == 0) continue;

                if (!TryResolveToken(token, out int vk))
                {
                    error = $"无法识别的按键：{token}";
                    return false;
                }

                if (seen.Add(vk)) keys.Add(vk);
            }

            if (keys.Count == 0)
            {
                error = "快捷键为空";
                return false;
            }

            // 修饰键排到前面，保证合成顺序正确
            keys.Sort((a, b) => ModifierRank(a).CompareTo(ModifierRank(b)));
            return true;
        }

        private static int ModifierRank(int vk) => vk switch
        {
            VK_CTRL => 0,
            VK_SHIFT => 1,
            VK_ALT => 2,
            VK_WIN => 3,
            _ => 9,
        };

        private static bool TryResolveToken(string token, out int vk)
        {
            if (Map.TryGetValue(token, out vk)) return true;

            // 单字母 A-Z
            if (token.Length == 1)
            {
                char c = char.ToUpperInvariant(token[0]);
                if (c >= 'A' && c <= 'Z') { vk = c; return true; }
                if (c >= '0' && c <= '9') { vk = c; return true; }
            }

            // F1 - F24
            if ((token[0] == 'F' || token[0] == 'f') && token.Length <= 3
                && int.TryParse(token.AsSpan(1), out int n) && n >= 1 && n <= 24)
            {
                vk = 0x70 + (n - 1);
                return true;
            }

            vk = 0;
            return false;
        }

        /// <summary>按键序列的可读形式。</summary>
        public static string Describe(IEnumerable<int> keys)
        {
            var sb = new StringBuilder();
            foreach (var vk in keys)
            {
                if (sb.Length > 0) sb.Append('+');
                sb.Append(NameOf(vk));
            }
            return sb.ToString();
        }

        public static string NameOf(int vk)
        {
            if (vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();
            if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();
            if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x70 + 1);

            return vk switch
            {
                VK_CTRL => "Ctrl",
                VK_SHIFT => "Shift",
                VK_ALT => "Alt",
                VK_WIN => "Win",
                0x1B => "Esc",
                0x09 => "Tab",
                0x0D => "Enter",
                0x20 => "Space",
                0x08 => "Backspace",
                0x2E => "Delete",
                0x2D => "Insert",
                0x24 => "Home",
                0x23 => "End",
                0x21 => "PageUp",
                0x22 => "PageDown",
                0x26 => "Up",
                0x28 => "Down",
                0x25 => "Left",
                0x27 => "Right",
                0x2C => "PrintScreen",
                0x13 => "Pause",
                0x14 => "CapsLock",
                0x90 => "NumLock",
                0x91 => "ScrollLock",
                0x5D => "Apps",
                0xAD => "Mute",
                0xAE => "VolumeDown",
                0xAF => "VolumeUp",
                0xB0 => "NextTrack",
                0xB1 => "PrevTrack",
                0xB2 => "StopMedia",
                0xB3 => "PlayPause",
                0xC0 => "`",
                0xBD => "-",
                0xBB => "=",
                0xDB => "[",
                0xDD => "]",
                0xDC => "\\",
                0xBA => ";",
                0xDE => "'",
                0xBC => ",",
                0xBE => ".",
                0xBF => "/",
                _ => "0x" + vk.ToString("X2"),
            };
        }

        // ================= 合成按键 =================

        private static NativeMethods.INPUT KeyInput(ushort vk, bool up)
        {
            uint flags = up ? NativeMethods.KEYEVENTF_KEYUP : 0;
            if (ExtendedKeys.Contains(vk)) flags |= NativeMethods.KEYEVENTF_EXTENDEDKEY;

            return new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_KEYBOARD,
                u = new NativeMethods.INPUTUNION
                {
                    ki = new NativeMethods.KEYBDINPUT
                    {
                        wVk = vk,
                        wScan = 0,
                        dwFlags = flags,
                        time = 0,
                        dwExtraInfo = NativeMethods.Signature,
                    },
                },
            };
        }

        /// <summary>按下并松开一整组按键（修饰键先按后松）。</summary>
        public static void Send(IReadOnlyList<int> keys)
        {
            if (keys.Count == 0) return;

            var inputs = new List<NativeMethods.INPUT>(keys.Count * 2);
            foreach (var vk in keys) inputs.Add(KeyInput((ushort)vk, false));
            for (int i = keys.Count - 1; i >= 0; i--) inputs.Add(KeyInput((ushort)keys[i], true));

            NativeMethods.SendInput((uint)inputs.Count, inputs.ToArray(),
                System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.INPUT>());
        }

        /// <summary>解析并发送。返回是否成功。</summary>
        public static bool SendText(string? text, out string error)
        {
            if (!TryParse(text, out var keys, out error)) return false;
            Send(keys);
            return true;
        }

        /// <summary>只按下（用于 Win 键重放）。</summary>
        public static void KeyDown(int vk)
        {
            var arr = new[] { KeyInput((ushort)vk, false) };
            NativeMethods.SendInput(1, arr, System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.INPUT>());
        }

        /// <summary>只松开。</summary>
        public static void KeyUp(int vk)
        {
            var arr = new[] { KeyInput((ushort)vk, true) };
            NativeMethods.SendInput(1, arr, System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.INPUT>());
        }

        /// <summary>发送一次鼠标中键点击（用于“轻点”重放）。</summary>
        public static void SendMiddleClick()
        {
            NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_MIDDLEDOWN, 0, 0, 0, NativeMethods.SignatureUInt);
            NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_MIDDLEUP, 0, 0, 0, NativeMethods.SignatureUInt);
        }

        /// <summary>发送 Unicode 文本（逐字符）。</summary>
        public static void SendUnicode(string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            var inputs = new List<NativeMethods.INPUT>(text.Length * 2);
            foreach (char c in text)
            {
                inputs.Add(new NativeMethods.INPUT
                {
                    type = NativeMethods.INPUT_KEYBOARD,
                    u = new NativeMethods.INPUTUNION
                    {
                        ki = new NativeMethods.KEYBDINPUT
                        {
                            wVk = 0,
                            wScan = c,
                            dwFlags = NativeMethods.KEYEVENTF_UNICODE,
                            time = 0,
                            dwExtraInfo = NativeMethods.Signature,
                        },
                    },
                });
                inputs.Add(new NativeMethods.INPUT
                {
                    type = NativeMethods.INPUT_KEYBOARD,
                    u = new NativeMethods.INPUTUNION
                    {
                        ki = new NativeMethods.KEYBDINPUT
                        {
                            wVk = 0,
                            wScan = c,
                            dwFlags = NativeMethods.KEYEVENTF_UNICODE | NativeMethods.KEYEVENTF_KEYUP,
                            time = 0,
                            dwExtraInfo = NativeMethods.Signature,
                        },
                    },
                });
            }

            NativeMethods.SendInput((uint)inputs.Count, inputs.ToArray(),
                System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.INPUT>());
        }
    }
}
