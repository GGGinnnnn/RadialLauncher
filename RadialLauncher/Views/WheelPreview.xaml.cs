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
    /// <summary>
    /// 外观页的实时预览。
    ///
    /// <para>绘制全部委托给 <see cref="WheelPainter"/> —— 和真实轮盘用的是同一套代码，
    /// 所以这里看到的层次（发光、双环、辐条、刻度、槽位高光）就是唤出时的样子。
    /// 之前预览是自己另画一遍，皮肤一改就会和实际不一致。</para>
    /// </summary>
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

            // fit 系数由 WheelGeometry 按“该方向数真实的窗口尺寸”算出，
            // 因此四向轮盘（窗口更大）也能完整显示，不会被裁掉一圈。
            double fit = WheelGeometry.FitScaleFor(_appearance, directionCount, size);
            var g = WheelGeometry.Compute(_appearance, directionCount, fit);

            var skin = WheelSkinCatalog.Get(_appearance.Skin);

            var backgroundColor = BrushHelper.ParseColor(
                _appearance.BackgroundColor, Color.FromArgb(0xE6, 0x1A, 0x1A, 0x2E));
            var borderColor = BrushHelper.ParseColor(
                _appearance.BorderColor, Color.FromRgb(0x4A, 0x9E, 0xFF));
            var itemColor = BrushHelper.ParseColor(
                _appearance.ItemColor, Color.FromArgb(0xE6, 0x2A, 0x2A, 0x3A));
            var selectedColor = BrushHelper.ParseColor(
                _appearance.ItemSelectedColor, Color.FromRgb(0xFF, 0x6B, 0x35));
            var textColor = BrushHelper.ParseColor(_appearance.TextColor, Colors.White);

            // 背景装饰（会把 canvas 清空，所以必须先画）
            WheelPainter.DrawBackground(Surface, g, skin, backgroundColor, borderColor, textColor,
                centerX, centerY, _appearance.ShowGlow);

            // 选中扇区要压在槽位下面，所以先画
            if (skin.RingLayout)
            {
                WheelPainter.DrawSelection(Surface, g, skin, selectedColor, textColor,
                    centerX, centerY, _selectedIndex);
            }

            // 槽位
            double itemSize = g.ItemSize;
            // 环形布局把图标推到外侧环带上（约 0.82R）
            double radius = skin.RingLayout ? g.RingSlotRadius : g.Radius;

            for (int i = 0; i < directionCount; i++)
            {
                double angle = -90 + (360.0 / directionCount) * i;
                double rad = angle * Math.PI / 180.0;

                double x = centerX + radius * Math.Cos(rad);
                double y = centerY + radius * Math.Sin(rad);

                bool isSelected = i == _selectedIndex;
                bool hasItem = i < _items.Count;
                var item = hasItem ? _items[i] : null;

                // 圆底：只有浮动圆钮布局才有；环形布局的槽位是"裸图标浮在环带上"
                if (!skin.RingLayout)
                {
                    var dot = new Ellipse
                    {
                        Width = itemSize,
                        Height = itemSize,
                        Fill = WheelPainter.SlotBrush(
                            isSelected ? selectedColor : itemColor, skin, isSelected),
                        Opacity = hasItem ? 1.0 : 0.42,
                        IsHitTestVisible = false,
                    };
                    Canvas.SetLeft(dot, x - itemSize / 2);
                    Canvas.SetTop(dot, y - itemSize / 2);
                    Surface.Children.Add(dot);
                }

                if (!hasItem) continue;

                // 高光 + 边光 + 图标 + 名字（与真实轮盘同一份实现）
                var content = WheelPainter.BuildSlotContent(item, itemSize, skin, textColor, isSelected);
                Canvas.SetLeft(content, x - itemSize / 2);
                Canvas.SetTop(content, y - itemSize / 2);
                Surface.Children.Add(content);
            }

            // 中心提示
            var hint = new TextBlock
            {
                Text = _items.Count == 0 ? Loc.T("Wheel_Empty") : Loc.T("Wheel_Release"),
                FontSize = 10.5,
                Foreground = new SolidColorBrush(Color.FromArgb(0xBB, textColor.R, textColor.G, textColor.B)),
                TextAlignment = TextAlignment.Center,
                IsHitTestVisible = false,
            };

            hint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(hint, centerX - hint.DesiredSize.Width / 2);
            Canvas.SetTop(hint, centerY - hint.DesiredSize.Height / 2);
            Surface.Children.Add(hint);
        }
    }
}
