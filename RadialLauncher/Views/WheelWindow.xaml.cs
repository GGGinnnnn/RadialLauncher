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

            int directionCount = wheel.ItemCount is 4 or 8 ? wheel.ItemCount : 8;
            double itemSize = appearance.ItemSize * scale;

            // 圆心到选项中心的距离：按周长能放下所有选项来推算，再留出呼吸感
            double radius = directionCount == 4 ? itemSize * 2.05 : itemSize * 1.95;
            double plateRadius = radius + itemSize * 0.72;
            double glowPadding = appearance.ShowGlow ? 36 : 0;
            double windowSize = 2 * (plateRadius + glowPadding);

            Width = windowSize;
            Height = windowSize;

            double plateDiameter = plateRadius * 2;

            GlowEllipse.Width = plateDiameter;
            GlowEllipse.Height = plateDiameter;
            GlowEllipse.Visibility = appearance.ShowGlow ? Visibility.Visible : Visibility.Collapsed;

            PlateEllipse.Width = plateDiameter;
            PlateEllipse.Height = plateDiameter;

            double innerDiameter = plateDiameter * 0.34;
            InnerEllipse.Width = innerDiameter;
            InnerEllipse.Height = innerDiameter;

            // 配色
            var backgroundBrush = BrushHelper.FromHex(appearance.BackgroundColor, Color.FromArgb(0xE6, 0x1A, 0x1A, 0x2E));
            var borderBrush = BrushHelper.FromHex(appearance.BorderColor, Color.FromRgb(0x4A, 0x9E, 0xFF));
            var itemBrush = BrushHelper.FromHex(appearance.ItemColor, Color.FromArgb(0xE6, 0x2A, 0x2A, 0x3A));
            var selectedBrush = BrushHelper.FromHex(appearance.ItemSelectedColor, Color.FromRgb(0xFF, 0x6B, 0x35));
            var textBrush = BrushHelper.FromHex(appearance.TextColor, Colors.White);

            PlateEllipse.Fill = backgroundBrush;
            PlateEllipse.Stroke = borderBrush;
            PlateEllipse.StrokeThickness = appearance.ShowGlow ? 2.5 : 2;

            if (GlowEllipse.Effect is System.Windows.Media.Effects.DropShadowEffect glow)
                glow.Color = ((SolidColorBrush)borderBrush).Color;

            InnerEllipse.Fill = new SolidColorBrush(((SolidColorBrush)textBrush).Color) { Opacity = 0.12 };

            Menu.Width = windowSize;
            Menu.Height = windowSize;
            Menu.ItemCount = Math.Max(1, directionCount);
            Menu.Radius = radius;
            Menu.DeadZone = Math.Max(12, appearance.DeadZone * scale);
            Menu.DefaultBrush = itemBrush;
            Menu.SelectedBrush = selectedBrush;

            // 自己的窗口要接收鼠标点击时不能穿透
            _clickThrough = appearance.ExecuteOnRelease;

            CenterTitle.Foreground = textBrush;
            CenterSubtitle.Foreground = new SolidColorBrush(((SolidColorBrush)textBrush).Color) { Opacity = 0.6 };
            CenterTitle.FontSize = Math.Max(11, 14 * scale);
            CenterSubtitle.FontSize = Math.Max(9, 11 * scale);
            CenterTitle.MaxWidth = radius * 0.9;

            // 重建选项
            Menu.Children.Clear();

            var buttonStyle = (Style)FindResource("WheelButtonStyle");
            int count = Math.Min(directionCount, _items.Count);

            for (int i = 0; i < count; i++)
            {
                var button = CreateItemButton(_items[i], itemSize, buttonStyle, textBrush, itemBrush);
                Menu.Children.Add(button);
            }

            // 选项换了，选中状态要清掉
            Menu.ClearSelection();
            UpdateCenterHint();
        }

        private Button CreateItemButton(
            WheelItem item, double itemSize, Style style, Brush textBrush, Brush itemBrush)
        {
            var content = BuildItemContent(item, itemSize, textBrush);

            var button = new Button
            {
                Style = style,
                Content = content,
                Width = itemSize,
                Height = itemSize,
                Background = itemBrush,
                BorderBrush = textBrush,
                ToolTip = string.IsNullOrWhiteSpace(item.Name) ? null : item.Name,
                RenderTransform = new ScaleTransform(1, 1),
                RenderTransformOrigin = new Point(0.5, 0.5),
                IsHitTestVisible = false,
            };

            return button;
        }

        private static UIElement BuildItemContent(WheelItem item, double itemSize, Brush textBrush)
        {
            var stack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var image = TryResolveIconImage(item, itemSize);

            if (image != null)
            {
                stack.Children.Add(new Image
                {
                    Source = image,
                    Width = itemSize * 0.44,
                    Height = itemSize * 0.44,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Center,
                });
            }
            else
            {
                stack.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(item.Icon) ? "●" : item.Icon,
                    FontSize = Math.Max(12, itemSize * 0.34),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Foreground = textBrush,
                    TextAlignment = TextAlignment.Center,
                });
            }

            if (!string.IsNullOrWhiteSpace(item.Name))
            {
                stack.Children.Add(new TextBlock
                {
                    Text = item.Name,
                    FontSize = Math.Max(8, itemSize * 0.128),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = itemSize * 0.94,
                    Foreground = textBrush,
                    Margin = new Thickness(0, 1, 0, 0),
                });
            }

            return stack;
        }

        /// <summary>
        /// 图标解析优先级：IconPath == "auto" 时自动从目标提取 → IconPath 是真实文件 → emoji。
        /// </summary>
        private static ImageSource? TryResolveIconImage(WheelItem item, double itemSize)
        {
            try
            {
                var iconPath = item.IconPath?.Trim() ?? "";

                if (string.Equals(iconPath, "auto", StringComparison.OrdinalIgnoreCase))
                {
                    var target = item.ActionType switch
                    {
                        "LaunchApp" => IconService.GuessExecutable(item.ActionValue),
                        "OpenFolder" => item.ActionValue,
                        "Script" => item.ActionValue,
                        _ => "",
                    };

                    var icon = IconService.GetIcon(target);
                    if (icon != null) return icon;
                }
                else if (!string.IsNullOrWhiteSpace(iconPath) && System.IO.File.Exists(iconPath))
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.UriSource = new Uri(iconPath, UriKind.Absolute);
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.DecodePixelWidth = (int)Math.Max(16, itemSize * 0.6);
                    bitmap.EndInit();
                    bitmap.Freeze();
                    return bitmap;
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"加载图标失败（{item.IconPath}）：{ex.Message}");
            }

            return null;
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
