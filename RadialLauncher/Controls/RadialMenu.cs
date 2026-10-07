using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RadialLauncher.Controls
{
    /// <summary>
    /// 把子元素均匀排布在一个圆周上的面板。
    /// 索引 0 永远在正上方，之后顺时针递增（四向：上/右/下/左；八向：上/右上/右/…/左上）。
    /// </summary>
    public class RadialMenu : Panel
    {
        public static readonly DependencyProperty ItemCountProperty =
            DependencyProperty.Register(nameof(ItemCount), typeof(int), typeof(RadialMenu),
                new FrameworkPropertyMetadata(8,
                    FrameworkPropertyMetadataOptions.AffectsArrange, OnLayoutChanged));

        public int ItemCount
        {
            get => (int)GetValue(ItemCountProperty);
            set => SetValue(ItemCountProperty, value);
        }

        public static readonly DependencyProperty RadiusProperty =
            DependencyProperty.Register(nameof(Radius), typeof(double), typeof(RadialMenu),
                new FrameworkPropertyMetadata(140.0,
                    FrameworkPropertyMetadataOptions.AffectsArrange, OnLayoutChanged));

        /// <summary>圆心到每个选项中心的距离。</summary>
        public double Radius
        {
            get => (double)GetValue(RadiusProperty);
            set => SetValue(RadiusProperty, value);
        }

        public static readonly DependencyProperty DeadZoneProperty =
            DependencyProperty.Register(nameof(DeadZone), typeof(double), typeof(RadialMenu),
                new PropertyMetadata(34.0));

        /// <summary>鼠标离圆心小于该半径时不选中任何项。</summary>
        public double DeadZone
        {
            get => (double)GetValue(DeadZoneProperty);
            set => SetValue(DeadZoneProperty, value);
        }

        public static readonly DependencyProperty SelectedBrushProperty =
            DependencyProperty.Register(nameof(SelectedBrush), typeof(Brush), typeof(RadialMenu),
                new PropertyMetadata(Brushes.OrangeRed));

        public Brush SelectedBrush
        {
            get => (Brush)GetValue(SelectedBrushProperty);
            set => SetValue(SelectedBrushProperty, value);
        }

        public static readonly DependencyProperty DefaultBrushProperty =
            DependencyProperty.Register(nameof(DefaultBrush), typeof(Brush), typeof(RadialMenu),
                new PropertyMetadata(Brushes.Transparent));

        public Brush DefaultBrush
        {
            get => (Brush)GetValue(DefaultBrushProperty);
            set => SetValue(DefaultBrushProperty, value);
        }

        public static readonly DependencyProperty StartAngleProperty =
            DependencyProperty.Register(nameof(StartAngle), typeof(double), typeof(RadialMenu),
                new FrameworkPropertyMetadata(-90.0,
                    FrameworkPropertyMetadataOptions.AffectsArrange, OnLayoutChanged));

        /// <summary>第一个选项的角度（-90 = 正上方）。</summary>
        public double StartAngle
        {
            get => (double)GetValue(StartAngleProperty);
            set => SetValue(StartAngleProperty, value);
        }

        private int _selectedIndex = -1;

        public int SelectedIndex => _selectedIndex;

        /// <summary>选中项变化（-1 表示没有选中）。</summary>
        public event EventHandler? SelectedIndexChanged;

        private static void OnLayoutChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var menu = (RadialMenu)d;
            menu.InvalidateMeasure();
            menu.InvalidateArrange();
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            foreach (UIElement child in InternalChildren)
                child.Measure(availableSize);

            double width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
            double height = double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height;
            return new Size(width, height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double centerX = finalSize.Width / 2;
            double centerY = finalSize.Height / 2;
            int count = Math.Max(1, ItemCount);

            for (int i = 0; i < InternalChildren.Count; i++)
            {
                var child = InternalChildren[i];
                double angle = StartAngle + (360.0 / count) * i;
                double rad = angle * Math.PI / 180.0;

                var size = child.DesiredSize;
                double x = centerX + Radius * Math.Cos(rad) - size.Width / 2;
                double y = centerY + Radius * Math.Sin(rad) - size.Height / 2;

                child.Arrange(new Rect(x, y, size.Width, size.Height));
            }

            return finalSize;
        }

        /// <summary>根据鼠标相对圆心的位置更新选中项。</summary>
        public void UpdateSelection(Point relativeToCenter)
        {
            double dx = relativeToCenter.X;
            double dy = relativeToCenter.Y;
            double dist = Math.Sqrt(dx * dx + dy * dy);

            if (dist < DeadZone || InternalChildren.Count == 0)
            {
                SetSelected(-1);
                return;
            }

            // atan2 得到以右方为 0、顺时针为正的角度；+90 让“正上方”变成 0
            double angle = Math.Atan2(dy, dx) * 180.0 / Math.PI;
            double normalized = (angle - StartAngle + 360.0) % 360.0;

            int count = Math.Max(1, ItemCount);
            double step = 360.0 / count;

            int index = (int)Math.Round(normalized / step, MidpointRounding.AwayFromZero) % count;

            // 只对实际存在的子元素生效
            if (index < 0 || index >= InternalChildren.Count) index = -1;

            SetSelected(index);
        }

        public void ClearSelection() => SetSelected(-1);

        private void SetSelected(int index)
        {
            if (_selectedIndex == index) return;

            ApplyBackground(_selectedIndex, DefaultBrush);
            _selectedIndex = index;
            ApplyBackground(_selectedIndex, SelectedBrush);

            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }

        private void ApplyBackground(int index, Brush brush)
        {
            if (index < 0 || index >= InternalChildren.Count) return;
            if (InternalChildren[index] is Control control) control.Background = brush;
        }

        /// <summary>子元素被替换后重新套用配色。</summary>
        public void RefreshBrushes()
        {
            foreach (UIElement child in InternalChildren)
            {
                if (child is Control control) control.Background = DefaultBrush;
            }

            ApplyBackground(_selectedIndex, SelectedBrush);
        }
    }
}
