using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace RadialLauncher.Services
{
    /// <summary>
    /// 查询 / 等待修饰键状态。
    ///
    /// 存在的意义：轮盘是“按住触发键唤出、松开触发键执行”的，
    /// 而触发键本身往往就是修饰键（Ctrl / Win）。
    /// 如果松开的那一刻立刻合成快捷键，Windows 可能还认为那个键是按下的，
    /// 于是 Win+V 会变成 Ctrl+Win+V —— 轻则没反应，重则触发系统提示音。
    /// 所以执行动作前要确认修饰键真的抬起来了。
    /// </summary>
    public static class KeyStateHelper
    {
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        /// <summary>会被带进合成快捷键、造成污染的所有修饰键。</summary>
        private static readonly int[] Modifiers =
        {
            NativeMethods.VK_CONTROL, NativeMethods.VK_LCONTROL, NativeMethods.VK_RCONTROL,
            NativeMethods.VK_SHIFT,   NativeMethods.VK_LSHIFT,   NativeMethods.VK_RSHIFT,
            NativeMethods.VK_MENU,    NativeMethods.VK_LMENU,    NativeMethods.VK_RMENU,
            NativeMethods.VK_LWIN,    NativeMethods.VK_RWIN,
        };

        public static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

        /// <summary>当前是否还有修饰键按着。</summary>
        public static bool AnyModifierDown()
        {
            foreach (var vk in Modifiers)
            {
                if (IsDown(vk)) return true;
            }
            return false;
        }

        /// <summary>当前按着的修饰键名称，用于日志。</summary>
        public static string DescribeDown()
        {
            var names = new List<string>();

            if (IsDown(NativeMethods.VK_CONTROL) || IsDown(NativeMethods.VK_LCONTROL) || IsDown(NativeMethods.VK_RCONTROL))
                names.Add("Ctrl");
            if (IsDown(NativeMethods.VK_SHIFT) || IsDown(NativeMethods.VK_LSHIFT) || IsDown(NativeMethods.VK_RSHIFT))
                names.Add("Shift");
            if (IsDown(NativeMethods.VK_MENU) || IsDown(NativeMethods.VK_LMENU) || IsDown(NativeMethods.VK_RMENU))
                names.Add("Alt");
            if (IsDown(NativeMethods.VK_LWIN) || IsDown(NativeMethods.VK_RWIN))
                names.Add("Win");

            return names.Count == 0 ? "无" : string.Join("+", names);
        }

        /// <summary>
        /// 等到所有修饰键都抬起来（最多等 maxMs 毫秒），返回实际等待的毫秒数。
        /// 通常一次就过，返回值是 0。
        /// </summary>
        public static int WaitForModifiersReleased(int maxMs = 250)
        {
            if (!AnyModifierDown()) return 0;

            long start = Environment.TickCount64;

            while (true)
            {
                // 前几轮先让出时间片快速自旋，避免为了几毫秒去睡一整个调度周期
                Thread.Sleep(0);

                if (!AnyModifierDown()) break;

                long elapsed = Environment.TickCount64 - start;
                if (elapsed >= maxMs) break;

                Thread.Sleep(1);
            }

            int waited = (int)(Environment.TickCount64 - start);
            return waited;
        }
    }
}
