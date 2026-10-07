using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RadialLauncher.Services;

namespace RadialLauncher.Views
{
    /// <summary>
    /// 全屏取色器：先把整个虚拟桌面抓进内存 DC，再让一个小放大镜窗口跟随鼠标实时采样。
    /// 因为是抓取后的快照，所以不会取到自己窗口的像素。
    ///
    /// 左键确认、右键或 Esc 取消。
    /// </summary>
    public partial class ScreenColorPickerWindow : Window
    {
        private const int SampleSize = 15;      // 放大镜采样边长（像素）
        private const int MagnifierDip = 120;   // 放大镜显示边长（DIP）
        private const int CursorOffset = 18;    // 放大镜离光标的距离

        private const int SM_XVIRTUALSCREEN = 76;
        private const int SM_YVIRTUALSCREEN = 77;
        private const int SM_CXVIRTUALSCREEN = 78;
        private const int SM_CYVIRTUALSCREEN = 79;

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int width, int height,
            IntPtr hdcSrc, int xSrc, int ySrc, uint rop);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private const uint SRCCOPY = 0x00CC0020;

        private readonly DispatcherTimer _timer;
        private readonly WriteableBitmap _bitmap;
        private readonly int[] _pixels = new int[SampleSize * SampleSize];

        private IntPtr _screenDc = IntPtr.Zero;
        private IntPtr _memDc = IntPtr.Zero;
        private IntPtr _bitmapHandle = IntPtr.Zero;

        private int _virtualX;
        private int _virtualY;
        private int _virtualWidth;
        private int _virtualHeight;

        private double _dpiScale = 1.0;

        private bool _leftWasDown;
        private bool _rightWasDown;

        public Color SelectedColor { get; private set; } = Colors.White;

        private ScreenColorPickerWindow()
        {
            InitializeComponent();

            _bitmap = new WriteableBitmap(SampleSize, SampleSize, 96, 96, PixelFormats.Bgra32, null);
            Magnifier.Source = _bitmap;

            HintText.Text = Loc.T("Color_EyedropperHint");

            _timer = new DispatcherTimer(DispatcherPriority.Input)
            {
                Interval = TimeSpan.FromMilliseconds(20),
            };
            _timer.Tick += (_, _) => OnPoll();

            Loaded += (_, _) => CaptureScreen();
            Closed += (_, _) => ReleaseGdi();
            SourceInitialized += (_, _) => ApplyNoActivate();
        }

        /// <summary>进入屏幕取色；取消返回 null。</summary>
        public static Color? Pick()
        {
            var window = new ScreenColorPickerWindow();
            return window.ShowDialog() == true ? window.SelectedColor : null;
        }

        private void ApplyNoActivate()
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;

                int exStyle = NativeMethods.GetWindowLongW(hwnd, NativeMethods.GWL_EXSTYLE);
                exStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
                NativeMethods.SetWindowLongW(hwnd, NativeMethods.GWL_EXSTYLE, exStyle);
            }
            catch { }
        }

        // ==================== 屏幕快照 ====================

        private void CaptureScreen()
        {
            try
            {
                _virtualX = NativeMethods.GetSystemMetrics(SM_XVIRTUALSCREEN);
                _virtualY = NativeMethods.GetSystemMetrics(SM_YVIRTUALSCREEN);
                _virtualWidth = Math.Max(1, NativeMethods.GetSystemMetrics(SM_CXVIRTUALSCREEN));
                _virtualHeight = Math.Max(1, NativeMethods.GetSystemMetrics(SM_CYVIRTUALSCREEN));

                _screenDc = NativeMethods.GetDC(IntPtr.Zero);
                _memDc = CreateCompatibleDC(_screenDc);
                _bitmapHandle = CreateCompatibleBitmap(_screenDc, _virtualWidth, _virtualHeight);
                SelectObject(_memDc, _bitmapHandle);

                BitBlt(_memDc, 0, 0, _virtualWidth, _virtualHeight,
                    _screenDc, _virtualX, _virtualY, SRCCOPY);

                Logger.Debug($"屏幕取色：已抓取 {_virtualWidth}x{_virtualHeight} @({_virtualX},{_virtualY})");
            }
            catch (Exception ex)
            {
                Logger.Error("抓取屏幕失败", ex);
            }

            _timer.Start();
            OnPoll();
        }

        private void ReleaseGdi()
        {
            _timer.Stop();

            try
            {
                if (_bitmapHandle != IntPtr.Zero) DeleteObject(_bitmapHandle);
                if (_memDc != IntPtr.Zero) DeleteDC(_memDc);
                if (_screenDc != IntPtr.Zero) NativeMethods.ReleaseDC(IntPtr.Zero, _screenDc);
            }
            catch { }

            _bitmapHandle = IntPtr.Zero;
            _memDc = IntPtr.Zero;
            _screenDc = IntPtr.Zero;
        }

        // ==================== 采样循环 ====================

        private void OnPoll()
        {
            if (_memDc == IntPtr.Zero) return;

            var (cx, cy) = MouseHelper.GetCursorPosition();

            int localX = cx - _virtualX;
            int localY = cy - _virtualY;

            var center = SamplePixel(localX, localY);
            SelectedColor = center;

            UpdateMagnifier(localX, localY);
            UpdateInfo(center, cx, cy);
            FollowCursor(cx, cy);

            // 左键确认
            bool leftDown = IsDown(NativeMethods.VK_LBUTTON);
            if (leftDown && !_leftWasDown)
            {
                _timer.Stop();
                DialogResult = true;
                Close();
                return;
            }
            _leftWasDown = leftDown;

            // 右键 / Esc 取消
            bool rightDown = IsDown(NativeMethods.VK_RBUTTON);
            if ((rightDown && !_rightWasDown) || IsDown(NativeMethods.VK_ESCAPE))
            {
                _timer.Stop();
                DialogResult = false;
                Close();
                return;
            }
            _rightWasDown = rightDown;
        }

        private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

        private Color SamplePixel(int x, int y)
        {
            try
            {
                if (x < 0 || y < 0 || x >= _virtualWidth || y >= _virtualHeight) return Colors.Black;

                uint pixel = NativeMethods.GetPixel(_memDc, x, y);
                return Color.FromRgb((byte)(pixel & 0xFF), (byte)((pixel >> 8) & 0xFF), (byte)((pixel >> 16) & 0xFF));
            }
            catch
            {
                return Colors.Black;
            }
        }

        private void UpdateMagnifier(int localX, int localY)
        {
            int half = SampleSize / 2;

            for (int row = 0; row < SampleSize; row++)
            {
                for (int col = 0; col < SampleSize; col++)
                {
                    var color = SamplePixel(localX + col - half, localY + row - half);

                    // Bgra32 小端：int 里是 BGRA
                    _pixels[row * SampleSize + col] =
                        (0xFF << 24) | (color.R << 16) | (color.G << 8) | color.B;
                }
            }

            _bitmap.WritePixels(
                new Int32Rect(0, 0, SampleSize, SampleSize),
                _pixels, SampleSize * 4, 0);
        }

        private void UpdateInfo(Color color, int x, int y)
        {
            Swatch.Background = new SolidColorBrush(color);
            HexText.Text = BrushHelper.ToHexNoAlpha(color);
            CoordText.Text = $"{x}, {y}";
        }

        /// <summary>把放大镜窗口贴到光标旁边（用物理像素定位，避免 DPI 换算误差）。</summary>
        private void FollowCursor(int cursorX, int cursorY)
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;

                // 只在第一次显示时算一次缩放，之后不再变化
                if (!IsVisible)
                {
                    _dpiScale = GetScaleForPoint(cursorX, cursorY);
                    Show();
                }

                int width = (int)Math.Round(Width * _dpiScale);
                int height = (int)Math.Round(Height * _dpiScale);

                int left = cursorX + CursorOffset;
                int top = cursorY + CursorOffset;

                // 贴边时翻到光标另一侧
                if (left + width > _virtualX + _virtualWidth) left = cursorX - width - CursorOffset;
                if (top + height > _virtualY + _virtualHeight) top = cursorY - height - CursorOffset;
                if (left < _virtualX) left = _virtualX;
                if (top < _virtualY) top = _virtualY;

                NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, left, top, width, height,
                    NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
            }
            catch (Exception ex)
            {
                Logger.Debug("定位取色器失败：" + ex.Message);
            }
        }

        private static double GetScaleForPoint(int x, int y)
        {
            try
            {
                var pt = new NativeMethods.POINT { X = x, Y = y };
                var monitor = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTONEAREST);
                if (monitor != IntPtr.Zero &&
                    NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MDT_EFFECTIVE_DPI,
                        out uint dpiX, out _) == 0 && dpiX > 0)
                {
                    return dpiX / 96.0;
                }
            }
            catch { }

            return 1.0;
        }
    }
}
