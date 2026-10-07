using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RadialLauncher.Services;

namespace RadialLauncher.Views
{
    /// <summary>HSV 取色器：色相/饱和度/明度面板 + 透明度 + 十六进制 + 屏幕取色。</summary>
    public partial class ColorPickerWindow : Window
    {
        private double _hue;
        private double _saturation = 1.0;
        private double _brightness = 1.0;
        private byte _alpha = 255;

        private bool _draggingSv;
        private bool _suppressHexEvent;
        private bool _suppressSliderEvent;

        public Color SelectedColor { get; private set; } = Colors.White;

        public ColorPickerWindow(Color initial)
        {
            InitializeComponent();

            BuildPresets();
            SetColor(initial, updateHue: true);
        }

        /// <summary>弹出取色器；取消时返回 null。</summary>
        public static Color? Pick(Window? owner, Color initial)
        {
            var window = new ColorPickerWindow(initial);
            if (owner != null && owner.IsVisible) window.Owner = owner;

            return window.ShowDialog() == true ? window.SelectedColor : null;
        }

        // ==================== 颜色同步 ====================

        private void SetColor(Color color, bool updateHue)
        {
            _alpha = color.A;

            if (updateHue)
            {
                RgbToHsv(color, out _hue, out _saturation, out _brightness);
            }
            else
            {
                HsvToRgb(_hue, _saturation, _brightness, out byte r, out byte g, out byte b);
                color = Color.FromArgb(_alpha, r, g, b);
            }

            SelectedColor = color;

            _suppressHexEvent = true;
            HexBox.Text = BrushHelper.ToHex(color);
            _suppressHexEvent = false;

            _suppressSliderEvent = true;
            HueSlider.Value = _hue;
            AlphaSlider.Value = _alpha;
            _suppressSliderEvent = false;

            AlphaText.Text = _alpha.ToString();

            UpdateHueLayer();
            UpdateThumb();
            UpdatePreview();
        }

        private void UpdateHueLayer()
        {
            HsvToRgb(_hue, 1, 1, out byte r, out byte g, out byte b);
            HueLayer.Fill = new SolidColorBrush(Color.FromRgb(r, g, b));
        }

        private void UpdateThumb()
        {
            double width = SvGrid.ActualWidth > 0 ? SvGrid.ActualWidth : 380;
            double height = SvGrid.ActualHeight > 0 ? SvGrid.ActualHeight : 180;

            Canvas.SetLeft(SvThumb, _saturation * width - SvThumb.Width / 2);
            Canvas.SetTop(SvThumb, (1 - _brightness) * height - SvThumb.Height / 2);

            SvThumb.Stroke = BrushHelper.IsLight(SelectedColor) ? Brushes.Black : Brushes.White;
        }

        private void UpdatePreview()
        {
            PreviewSwatch.Background = new SolidColorBrush(SelectedColor);
        }

        private void OnSvSizeChanged(object sender, SizeChangedEventArgs e) => UpdateThumb();

        // ==================== 交互 ====================

        private void OnSvMouseDown(object sender, MouseButtonEventArgs e)
        {
            _draggingSv = true;
            SvGrid.CaptureMouse();
            ApplySvFromPoint(e.GetPosition(SvGrid));
        }

        private void OnSvMouseMove(object sender, MouseEventArgs e)
        {
            if (_draggingSv) ApplySvFromPoint(e.GetPosition(SvGrid));
        }

        private void OnSvMouseUp(object sender, MouseButtonEventArgs e)
        {
            _draggingSv = false;
            SvGrid.ReleaseMouseCapture();
        }

        private void ApplySvFromPoint(Point point)
        {
            double width = SvGrid.ActualWidth > 0 ? SvGrid.ActualWidth : 380;
            double height = SvGrid.ActualHeight > 0 ? SvGrid.ActualHeight : 180;

            _saturation = Math.Clamp(point.X / width, 0, 1);
            _brightness = Math.Clamp(1 - point.Y / height, 0, 1);

            HsvToRgb(_hue, _saturation, _brightness, out byte r, out byte g, out byte b);
            SetColor(Color.FromArgb(_alpha, r, g, b), updateHue: false);
        }

        private void OnHueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressSliderEvent) return;

            _hue = e.NewValue;
            HsvToRgb(_hue, _saturation, _brightness, out byte r, out byte g, out byte b);
            SetColor(Color.FromArgb(_alpha, r, g, b), updateHue: false);
        }

        private void OnAlphaChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressSliderEvent) return;

            _alpha = (byte)Math.Clamp((int)Math.Round(e.NewValue), 0, 255);
            SetColor(Color.FromArgb(_alpha, SelectedColor.R, SelectedColor.G, SelectedColor.B), updateHue: false);
        }

        private void OnHexChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressHexEvent) return;

            if (BrushHelper.TryParseColor(HexBox.Text, out var color))
                SetColor(color, updateHue: true);
        }

        private void OnEyedropper(object sender, RoutedEventArgs e)
        {
            // 取色时需要短暂隐藏自己，否则会取到本窗口的像素
            var wasVisible = IsVisible;
            Hide();

            try
            {
                var picked = ScreenColorPickerWindow.Pick();
                if (picked.HasValue)
                {
                    SetColor(Color.FromArgb(_alpha, picked.Value.R, picked.Value.G, picked.Value.B), updateHue: true);
                }
            }
            finally
            {
                if (wasVisible) Show();
                Activate();
            }
        }

        private void OnOk(object sender, RoutedEventArgs e)
        {
            SelectedColor = BrushHelper.ParseColor(HexBox.Text, SelectedColor);
            DialogResult = true;
            Close();
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        // ==================== 预设色板 ====================

        private static readonly string[] Presets =
        {
            "#FF4A9EFF", "#FF2D89EF", "#FF00B7C3", "#FF00CC6A", "#FF7BD800", "#FFFFB900",
            "#FFFF6B35", "#FFE81123", "#FFB4009E", "#FF8E44AD", "#FF6C5CE7", "#FF34495E",
            "#FFFFFFFF", "#FFD9D9D9", "#FF9E9E9E", "#FF5A5A5A", "#FF2A2A3A", "#FF1A1A2E",
            "#CC1A1A2E", "#E62A2A3A", "#80000000", "#00000000",
        };

        private void BuildPresets()
        {
            foreach (var hex in Presets)
            {
                var color = BrushHelper.ParseColor(hex, Colors.White);

                var swatch = new Border
                {
                    Width = 30,
                    Height = 30,
                    Margin = new Thickness(0, 0, 6, 6),
                    CornerRadius = new CornerRadius(4),
                    BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0)),
                    Background = CreateCheckerBrush(color),
                    Cursor = Cursors.Hand,
                    ToolTip = hex,
                };

                swatch.MouseLeftButtonUp += (_, _) => SetColor(color, updateHue: true);
                PresetList.Items.Add(swatch);
            }
        }

        /// <summary>带棋盘格衬底的画刷，半透明颜色也能看清。</summary>
        private static Brush CreateCheckerBrush(Color color)
        {
            var group = new DrawingGroup();
            group.Children.Add(new GeometryDrawing(
                Brushes.White, null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
            group.Children.Add(new GeometryDrawing(
                new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)), null,
                new RectangleGeometry(new Rect(0, 0, 8, 8))));
            group.Children.Add(new GeometryDrawing(
                new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)), null,
                new RectangleGeometry(new Rect(8, 8, 8, 8))));
            group.Children.Add(new GeometryDrawing(
                new SolidColorBrush(color), null, new RectangleGeometry(new Rect(0, 0, 16, 16))));

            var brush = new DrawingBrush(group)
            {
                TileMode = TileMode.Tile,
                Viewport = new Rect(0, 0, 16, 16),
                ViewportUnits = BrushMappingMode.Absolute,
                Stretch = Stretch.None,
            };
            brush.Freeze();
            return brush;
        }

        // ==================== 颜色空间转换 ====================

        public static void RgbToHsv(Color color, out double hue, out double saturation, out double value)
        {
            double r = color.R / 255.0;
            double g = color.G / 255.0;
            double b = color.B / 255.0;

            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            double delta = max - min;

            value = max;
            saturation = max <= 0 ? 0 : delta / max;

            if (delta <= 0) hue = 0;
            else if (max == r) hue = 60 * (((g - b) / delta) % 6);
            else if (max == g) hue = 60 * ((b - r) / delta + 2);
            else hue = 60 * ((r - g) / delta + 4);

            if (hue < 0) hue += 360;
        }

        public static void HsvToRgb(double hue, double saturation, double value,
            out byte r, out byte g, out byte b)
        {
            hue = ((hue % 360) + 360) % 360;
            saturation = Math.Clamp(saturation, 0, 1);
            value = Math.Clamp(value, 0, 1);

            double c = value * saturation;
            double x = c * (1 - Math.Abs((hue / 60.0 % 2) - 1));
            double m = value - c;

            double rr, gg, bb;
            switch ((int)(hue / 60))
            {
                case 0: rr = c; gg = x; bb = 0; break;
                case 1: rr = x; gg = c; bb = 0; break;
                case 2: rr = 0; gg = c; bb = x; break;
                case 3: rr = 0; gg = x; bb = c; break;
                case 4: rr = x; gg = 0; bb = c; break;
                default: rr = c; gg = 0; bb = x; break;
            }

            r = (byte)Math.Round((rr + m) * 255);
            g = (byte)Math.Round((gg + m) * 255);
            b = (byte)Math.Round((bb + m) * 255);
        }
    }
}
