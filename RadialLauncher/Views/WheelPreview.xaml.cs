using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using RadialLauncher.Models;
using RadialLauncher.Services;

namespace RadialLauncher.Views
{
    /// <summary>外观页的实时预览：按当前配置画一个小号的轮盘。</summary>
    public partial class WheelPreview : UserControl
    {
        private AppearanceSettings _appearance = new();
        private List<WheelItem> _items = new();
        private int _selectedIndex;

        public WheelPreview()
        {
            InitializeComponent();
            SizeChanged += (_, _) => Render();
        }

        /// <summary>设置预览内容。</summary>
        public void Render(AppearanceSettings appearance, List<WheelItem> items, int selectedIndex = -1)
        {
            _appearance = appearance ?? new AppearanceSettings();
            _items = items ?? new List<WheelItem>();
            _selectedIndex = selectedIndex;
            Render();
        }

        private void Render()
        {
            Surface.Children.Clear();

            double width = ActualWidth > 20 ? ActualWidth : 340;
            double height = ActualHeight > 20 ? ActualHeight : 260;
            double size = Math.Min(width, height);
            double centerX = width / 2;
            double centerY = height / 2;

            int directionCount = WheelGeometry.NormalizeDirectionCount(_appearance.ItemCount);

            // 缩放以适应预览区域。fit 系数由 WheelGeometry 按“该方向数真实的窗口尺寸”算出，
            // 因此四向轮盘（窗口更大）也能完整显示，不会被裁掉一圈。
            double fit = WheelGeometry.FitScaleFor(_appearance, directionCount, size);

            var g = WheelGeometry.Compute(_appearance, directionCount, fit);
            double itemSize = g.ItemSize;
            double radius = g.Radius;
            double plateRadius = g.PlateRadius;

            var background = BrushHelper.ParseColor(_appearance.BackgroundColor, Color.FromArgb(0xE6, 0x1A, 0x1A, 0x2E));
            var border = BrushHelper.ParseColor(_appearance.BorderColor, Color.FromRgb(0x4A, 0x9E, 0xFF));
            var itemColor = BrushHelper.ParseColor(_appearance.ItemColor, Color.FromArgb(0xE6, 0x2A, 0x2A, 0x3A));
            var selectedColor = BrushHelper.ParseColor(_appearance.ItemSelectedColor, Color.FromRgb(0xFF, 0x6B, 0x35));
            var textColor = BrushHelper.ParseColor(_appearance.TextColor, Colors.White);

            // 底板
            var plate = new Ellipse
            {
                Width = plateRadius * 2,
                Height = plateRadius * 2,
                Fill = new SolidColorBrush(background),
                Stroke = new SolidColorBrush(border),
                StrokeThickness = 1.5,
            };

            if (_appearance.ShowGlow)
            {
                plate.Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = border,
                    BlurRadius = Math.Max(8, plateRadius * 0.25),
                    ShadowDepth = 0,
                    Opacity = 0.65,
                };
            }

            Canvas.SetLeft(plate, centerX - plateRadius);
            Canvas.SetTop(plate, centerY - plateRadius);
            Surface.Children.Add(plate);

            // 选项
            for (int i = 0; i < directionCount; i++)
            {
                double angle = -90 + (360.0 / directionCount) * i;
                double rad = angle * Math.PI / 180.0;

                double x = centerX + radius * Math.Cos(rad);
                double y = centerY + radius * Math.Sin(rad);

                bool isSelected = i == _selectedIndex;
                bool hasItem = i < _items.Count;

                var dot = new Ellipse
                {
                    Width = itemSize,
                    Height = itemSize,
                    Fill = new SolidColorBrush(isSelected ? selectedColor : itemColor),
                    Stroke = new SolidColorBrush(Color.FromArgb(0x66, textColor.R, textColor.G, textColor.B)),
                    StrokeThickness = 1,
                    Opacity = hasItem ? 1.0 : 0.35,
                };

                Canvas.SetLeft(dot, x - itemSize / 2);
                Canvas.SetTop(dot, y - itemSize / 2);
                Surface.Children.Add(dot);

                if (!hasItem) continue;

                var item = _items[i];

                var label = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(item.Icon) ? "●" : item.Icon,
                    FontSize = Math.Max(9, itemSize * WheelGeometry.IconFontFactor),
                    Foreground = new SolidColorBrush(textColor),
                    TextAlignment = TextAlignment.Center,
                    Width = itemSize,
                };

                label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(label, x - itemSize / 2);
                Canvas.SetTop(label, y - label.DesiredSize.Height / 2);
                Surface.Children.Add(label);
            }

            // 中心提示
            var hint = new TextBlock
            {
                Text = _items.Count == 0 ? Loc.T("Wheel_Empty") : Loc.T("Wheel_Release"),
                FontSize = 10.5,
                Foreground = new SolidColorBrush(Color.FromArgb(0xBB, textColor.R, textColor.G, textColor.B)),
                TextAlignment = TextAlignment.Center,
            };

            hint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(hint, centerX - hint.DesiredSize.Width / 2);
            Canvas.SetTop(hint, centerY - hint.DesiredSize.Height / 2);
            Surface.Children.Add(hint);
        }
    }
}
