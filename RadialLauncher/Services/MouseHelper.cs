using System.Runtime.InteropServices;

namespace RadialLauncher.Services
{
    /// <summary>鼠标位置与按键状态查询。</summary>
    public static class MouseHelper
    {
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        /// <summary>获取鼠标屏幕坐标（物理像素）。</summary>
        public static (int X, int Y) GetCursorPosition()
        {
            NativeMethods.GetCursorPos(out var p);
            return (p.X, p.Y);
        }

        /// <summary>左键当前是否按下。</summary>
        public static bool IsLeftButtonDown()
            => (GetAsyncKeyState(NativeMethods.VK_LBUTTON) & 0x8000) != 0;

        /// <summary>
        /// 左键“自上次查询以来被按过”——GetAsyncKeyState 的最低位就是这个含义，
        /// 读取后会自动清零。
        ///
        /// 轮盘是靠 16ms 轮询来发现点击的，如果只看“当前是否按下”，
        /// 一次干脆的快速单击（按下到松开只有几毫秒）会整个漏掉。
        /// 用这个位就能把两次轮询之间的点击补回来。
        ///
        /// 注意：同一个键的任何一次 GetAsyncKeyState 调用都会清掉这一位，
        /// 所以不要在同一轮里先调 IsLeftButtonDown 再调它。
        /// </summary>
        public static bool ConsumeLeftButtonClick()
            => (GetAsyncKeyState(NativeMethods.VK_LBUTTON) & 0x0001) != 0;
    }
}
