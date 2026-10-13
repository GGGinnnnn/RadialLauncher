using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace RadialLauncher.Services
{
    /// <summary>
    /// 查询修饰键状态 + 必要时强制释放。
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
        /// 强制补发所有当前仍按着的修饰键“松开”。
        ///
        /// 用途：等待修饰键抬起的兜底路径超时后调用。此时用户的物理按键状态表
        /// 已经和真实意图不同步（例如松开被系统延迟上报），与其带着残留修饰键去合成
        /// 快捷键（Win+V 会变成 Ctrl+Win+V），不如主动把它们抬起来，让这一次执行
        /// 的结果是确定的。补发后由 <see cref="AnyModifierDown"/> 复核返回值即可。
        /// </summary>
        /// <returns>补发过松开的修饰键名称；都没补发则返回空列表。</returns>
        public static IReadOnlyList<string> ForceReleaseAllDown()
        {
            var released = new List<string>();

            void Release(string name, params int[] vks)
            {
                // 只要泛键或任一侧键仍按着，就把它们全部抬起（重复 KeyUp 无害）。
                bool down = false;
                foreach (var vk in vks)
                {
                    if (IsDown(vk)) { down = true; break; }
                }
                if (!down) return;

                foreach (var vk in vks)
                {
                    KeySequence.KeyUp(vk);
                }
                released.Add(name);
            }

            Release("Ctrl", NativeMethods.VK_CONTROL, NativeMethods.VK_LCONTROL, NativeMethods.VK_RCONTROL);
            Release("Shift", NativeMethods.VK_SHIFT, NativeMethods.VK_LSHIFT, NativeMethods.VK_RSHIFT);
            Release("Alt", NativeMethods.VK_MENU, NativeMethods.VK_LMENU, NativeMethods.VK_RMENU);
            Release("Win", NativeMethods.VK_LWIN, NativeMethods.VK_RWIN);

            return released;
        }
    }
}
