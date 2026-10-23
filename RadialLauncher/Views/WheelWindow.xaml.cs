using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RadialLauncher.Models;
using RadialLauncher.Services;

namespace RadialLauncher.Views
{
    /// <summary>
    /// 唤出在鼠标位置的轮盘窗口。
    /// 通过轮询鼠标位置来选中（窗口不抢焦点、不做命中测试，鼠标事件全部穿透到下层窗口）。
    /// </summary>
    public partial class WheelWindow : Window
    {
        private readonly DispatcherTimer _mouseTimer;

        private List<WheelItem> _items = new();
        private AppConfig? _config;

        /// <summary>唤出时鼠标所在的物理坐标，作为轮盘圆心。</summary>
        private int _centerPhysX;
        private int _centerPhysY;

        /// <summary>目标显示器的 DPI 缩放（1.0 = 96 DPI）。</summary>
        private double _dpiScale = 1.0;

        private double _targetOpacity = 1.0;
        private bool _clickThrough = true;

        // 环形布局下"高亮整个扇区"要用到的绘制参数
        private WheelMetrics? _metrics;
        private WheelSkin _skin = WheelSkinCatalog.Default;
        private Color _accentColor = Colors.OrangeRed;
        private Color _textColor = Colors.White;

        /// <summary>用户在轮盘上单击了某个选项。</summary>
        public event EventHandler<WheelItem>? ItemActivated;

        public WheelWindow()
        {
            InitializeComponent();

            _mouseTimer = new DispatcherTimer(DispatcherPriority.Input)
            {
                Interval = TimeSpan.FromMilliseconds(16),
            };
            _mouseTimer.Tick += (_, _) => OnPoll();

            Menu.SelectedIndexChanged += (_, _) => UpdateCenterHint();
            SourceInitialized += OnSourceInitialized;
        }

        public bool IsWheelVisible { get; private set; }

        /// <summary>当前选中的功能项，未选中返回 null。</summary>
        public WheelItem? SelectedItem
        {
            get
            {
                int i = Menu.SelectedIndex;
                if (i < 0 || i >= _items.Count) return null;
                return _items[i];
            }
        }

        // ============================ 显示 / 隐藏 ============================

        /// <summary>在鼠标当前位置显示轮盘。</summary>
        public void ShowWheel(AppConfig config, ActiveWheel wheel)
        {
            _config = config;

            var (mx, my) = MouseHelper.GetCursorPosition();
            _centerPhysX = mx;
            _centerPhysY = my;
            _dpiScale = GetScaleForPoint(mx, my);

            LoadWheel(config, wheel);

            // 先以完全透明的样子显示再定位，避免在旧位置闪一帧
            Root.Opacity = 0;
            Show();

            PositionAtCursor();
            ApplyExtendedStyles();

            _targetOpacity = config.Appearance.Opacity;
            PlayOpenAnimation(config);

            // “点击执行”模式下先清掉一次残留的点击标志，
            // 免得把唤出轮盘之前的那一下算成“点了选项”。
            if (!_clickThrough) MouseHelper.ConsumeLeftButtonClick();

            IsWheelVisible = true;
            _mouseTimer.Start();

            OnPoll();
        }

        /// <summary>隐藏轮盘。</summary>
        public void HideWheel()
        {
            _mouseTimer.Stop();
            IsWheelVisible = false;

            // 清掉动画，避免下次显示时残留
            Root.BeginAnimation(OpacityProperty, null);
            RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

            Hide();
        }

        private void PositionAtCursor()
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;

                int physW = (int)Math.Round(Width * _dpiScale);
                int physH = (int)Math.Round(Height * _dpiScale);

                NativeMethods.SetWindowPos(
                    hwnd,
                    NativeMethods.HWND_TOPMOST,
                    _centerPhysX - physW / 2,
                    _centerPhysY - physH / 2,
                    physW,
                    physH,
                    NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
            }
            catch (Exception ex)
            {
                Logger.Error("定位轮盘窗口失败", ex);
            }
        }

        private void ApplyExtendedStyles()
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;

                int exStyle = NativeMethods.GetWindowLongW(hwnd, NativeMethods.GWL_EXSTYLE);
                exStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;

                if (_clickThrough) exStyle |= NativeMethods.WS_EX_TRANSPARENT;
                else exStyle &= ~NativeMethods.WS_EX_TRANSPARENT;

                NativeMethods.SetWindowLongW(hwnd, NativeMethods.GWL_EXSTYLE, exStyle);
            }
            catch (Exception ex)
            {
                Logger.Debug("设置窗口扩展样式失败：" + ex.Message);
            }
        }

        private void OnSourceInitialized(object? sender, EventArgs e) => ApplyExtendedStyles();

        /// <summary>取鼠标所在显示器的 DPI 缩放。</summary>
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
            catch
            {
                // shcore.dll 不可用（很旧的系统）时退回 100%
            }

            return 1.0;
        }

        private void PlayOpenAnimation(AppConfig config)
        {
            double target = _targetOpacity;

            if (!config.Trigger.EnableAnimation)
            {
                RootScale.ScaleX = 1;
                RootScale.ScaleY = 1;
                Root.Opacity = target;
                return;
            }

            var duration = TimeSpan.FromMilliseconds(140);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

            Root.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, target, duration) { EasingFunction = ease });

            RootScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(0.84, 1, duration) { EasingFunction = ease });

            RootScale.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(0.84, 1, duration) { EasingFunction = ease });
        }

        // ============================ 构建轮盘 ============================

        private void LoadWheel(AppConfig config, ActiveWheel wheel)
        {
            _items = wheel.Items ?? new List<WheelItem>();

            var appearance = config.Appearance;
            double scale = appearance.Scale;

            int directionCount = WheelGeometry.NormalizeDirectionCount(wheel.ItemCount);

            // 几何计算统一走 WheelGeometry，与设置面板的预览共用同一套系数
            var g = WheelGeometry.Compute(appearance, directionCount);
            double itemSize = g.ItemSize;
            double radius = g.Radius;
            double windowSize = g.WindowSize;

            Width = windowSize;
            Height = windowSize;

            // 皮肤只决定装饰风格（辐条/双环/渐变强度），颜色仍来自外观设置里的五个字段
            var skin = WheelSkinCatalog.Get(appearance.Skin);

            var backgroundColor = BrushHelper.ParseColor(
                appearance.BackgroundColor, Color.FromArgb(0xE6, 0x1A, 0x1A, 0x2E));
            var borderColor = BrushHelper.ParseColor(
                appearance.BorderColor, Color.FromRgb(0x4A, 0x9E, 0xFF));
            var itemColor = BrushHelper.ParseColor(
                appearance.ItemColor, Color.FromArgb(0xE6, 0x2A, 0x2A, 0x3A));
            var selectedColor = BrushHelper.ParseColor(
                appearance.ItemSelectedColor, Color.FromRgb(0xFF, 0x6B, 0x35));
            var textColor = BrushHelper.ParseColor(appearance.TextColor, Colors.White);

            var textBrush = new SolidColorBrush(textColor);

            _metrics = g;
            _skin = skin;
            _accentColor = selectedColor;
            _textColor = textColor;

            // 环形布局没有"圆钮底"，槽位就是一尊图标浮在环带上；
            // 选中反馈靠背景层画的扇形高亮，所以这里底刷一律透明。
            var itemBrush = skin.RingLayout
                ? (Brush)Brushes.Transparent
                : WheelPainter.SlotBrush(itemColor, skin, selected: false);
            var selectedBrush = skin.RingLayout
                ? (Brush)Brushes.Transparent
                : WheelPainter.SlotBrush(selectedColor, skin, selected: true);

            // 背景装饰：外发光 / 底盘径向渐变 / 双环 / 辐条 / 中心刻度 / 中心盘
            Decor.Width = windowSize;
            Decor.Height = windowSize;
            WheelPainter.DrawBackground(Decor, g, skin, backgroundColor, borderColor, textColor,
                windowSize / 2, windowSize / 2, appearance.ShowGlow);

            Menu.Width = windowSize;
            Menu.Height = windowSize;
            Menu.ItemCount = Math.Max(1, directionCount);
            // 环形布局把图标推到外侧环带上（约 0.82R），浮钮布局用原来的半径
            Menu.Radius = skin.RingLayout ? g.RingSlotRadius : radius;
            Menu.DeadZone = g.DeadZone;
            Menu.DefaultBrush = itemBrush;
            Menu.SelectedBrush = selectedBrush;

            // 自己的窗口要接收鼠标点击时不能穿透
            _clickThrough = appearance.ExecuteOnRelease;

            CenterTitle.Foreground = textBrush;
            CenterSubtitle.Foreground = new SolidColorBrush(((SolidColorBrush)textBrush).Color) { Opacity = 0.6 };

            if (skin.RingLayout)
            {
                // 环形布局中间是一大片空区，文字可以大一些、宽一些
                CenterTitle.FontSize = Math.Max(12, 17 * scale);
                CenterSubtitle.FontSize = Math.Max(9, 11.5 * scale);
                CenterTitle.MaxWidth = g.PlateRadius * 1.55;
                CenterSubtitle.MaxWidth = g.PlateRadius * 1.55;
            }
            else
            {
                CenterTitle.FontSize = Math.Max(11, 14 * scale);
                CenterSubtitle.FontSize = Math.Max(9, 11 * scale);
                CenterTitle.MaxWidth = g.InnerDiameter * 0.80;
                CenterSubtitle.MaxWidth = g.InnerDiameter * 0.80;
            }

            // 重建选项
            Menu.Children.Clear();

            var buttonStyle = (Style)FindResource("WheelButtonStyle");
            int count = Math.Min(directionCount, _items.Count);

            for (int i = 0; i < count; i++)
            {
                var content = WheelPainter.BuildSlotContent(
                    _items[i], itemSize, skin, textColor, selected: false);

                var button = new Button
                {
                    Style = buttonStyle,
                    Content = content,
                    Width = itemSize,
                    Height = itemSize,
                    Background = itemBrush,
                    // 环形布局不要那圈内阴影，否则每个透明槽位都会显出个黑圆环
                    BorderBrush = skin.RingLayout ? Brushes.Transparent : new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)),
                    BorderThickness = new Thickness(skin.RingLayout ? 0 : 2),
                    ToolTip = string.IsNullOrWhiteSpace(_items[i].Name) ? null : _items[i].Name,
                    RenderTransform = new ScaleTransform(1, 1),
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    IsHitTestVisible = false,
                };

                Menu.Children.Add(button);
            }

            // 选项换了，选中状态要清掉
            Menu.ClearSelection();
            UpdateCenterHint();
        }

        // ============================ 选中与交互 ============================

        private void OnPoll()
        {
            if (!IsWheelVisible) return;

            var (mx, my) = MouseHelper.GetCursorPosition();

            double dx = (mx - _centerPhysX) / _dpiScale;
            double dy = (my - _centerPhysY) / _dpiScale;

            Menu.UpdateSelection(new Point(dx, dy));

            // 单击执行（默认是“松开触发键执行”，此时窗口可穿透，不处理点击）。
            // 这里用“自上次查询以来按下过”而不是“当前是否按下”：
            // 16ms 的轮询周期很容易整个错过一次干脆的快速单击。
            if (!_clickThrough && MouseHelper.ConsumeLeftButtonClick())
            {
                var item = SelectedItem;
                if (item != null) ItemActivated?.Invoke(this, item);
            }
        }

        private void UpdateCenterHint()
        {
            var item = SelectedItem;

            UpdateSectorHighlight();

            if (item != null)
            {
                CenterTitle.Text = item.Name;
                CenterSubtitle.Text = Loc.T("Wheel_Cancel");
                AnimateSelection();
                return;
            }

            CenterTitle.Text = _items.Count == 0 ? Loc.T("Wheel_Empty") : Loc.T("Wheel_Release");
            CenterSubtitle.Text = Loc.T("Wheel_Cancel");
            AnimateSelection();
        }

        /// <summary>
        /// 环形布局下把选中的扇区整块点亮（浮动圆钮布局不需要，由换背景色负责）。
        /// </summary>
        private void UpdateSectorHighlight()
        {
            if (_metrics == null || !_skin.RingLayout) return;

            WheelPainter.DrawSelection(Decor, _metrics, _skin, _accentColor, _textColor,
                Width / 2, Height / 2, Menu.SelectedIndex);
        }

        /// <summary>选中的选项弹一下，给点反馈。</summary>
        private void AnimateSelection()
        {
            if (_config is { Trigger.EnableAnimation: false }) return;

            int selected = Menu.SelectedIndex;

            for (int i = 0; i < Menu.Children.Count; i++)
            {
                if (Menu.Children[i] is not Button button) continue;
                if (button.RenderTransform is not ScaleTransform transform) continue;

                double target = i == selected ? 1.18 : 1.0;

                if (_config is { Trigger.EnableAnimation: true })
                {
                    var animation = new DoubleAnimation(target, TimeSpan.FromMilliseconds(110))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                    };
                    transform.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
                    transform.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
                }
                else
                {
                    transform.ScaleX = target;
                    transform.ScaleY = target;
                }
            }
        }
    }
}
