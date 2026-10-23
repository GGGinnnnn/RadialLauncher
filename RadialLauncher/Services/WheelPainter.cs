using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using RadialLauncher.Models;

namespace RadialLauncher.Services
{
    /// <summary>
    /// 轮盘绘制。真实窗口与设置面板预览共用这一份，保证两处一模一样。
    ///
    /// <para>两种布局，由 <see cref="WheelSkin.RingLayout"/> 决定：</para>
    /// <list type="bullet">
    ///   <item><b>环形（原神皮肤）</b>：由内到外是
    ///   「一大片空区（0 ~ 0.50R）→ 浅色圆环（0.50R ~ 0.66R）→
    ///   图标环带（0.66R ~ 0.98R，被细径向线切成扇区，图标落在 0.82R）→ 序号」。
    ///   中心不画实心圆盘，空区就是空的 —— 名字只在中心显示。
    ///   选中时高亮整个扇区。</item>
    ///   <item><b>浮动圆钮（默认皮肤）</b>：每个槽位是一枚带渐变的圆钮，
    ///   选中时整枚换成高亮渐变。</item>
    /// </list>
    /// </summary>
    public static class WheelPainter
    {
        /// <summary>环带外缘相对底盘半径的比例。</summary>
        public static double RingOuterRatio => WheelGeometry.RingBandOuterRatio;

        /// <summary>环带内缘相对底盘半径的比例。</summary>
        public static double RingInnerRatio => WheelGeometry.RingBandInnerRatio;

        /// <summary>序号所在半径（相对底盘半径）。</summary>
        public static double BadgeRadiusRatio => WheelGeometry.RingBadgeRadiusRatio;

        // ============================ 背景 ============================

        /// <summary>画整块背景装饰。会先清空 canvas。</summary>
        public static void DrawBackground(
            Canvas canvas,
            WheelMetrics m,
            WheelSkin skin,
            Color plate,
            Color rim,
            Color text,
            double centerX,
            double centerY,
            bool showGlow)
        {
            canvas.Children.Clear();

            double plateR = m.PlateRadius;
            double diameter = m.PlateDiameter;

            DrawGlow(canvas, m, skin, rim, centerX, centerY, diameter, showGlow);
            DrawDisc(canvas, m, skin, plate, rim, centerX, centerY, diameter);

            if (skin.RingLayout)
            {
                // 中间那圈浅色圆环 + 外侧图标环带；中间不画任何实体圆盘
                DrawDonut(canvas, m, skin, text, centerX, centerY, plateR);
                DrawRingBand(canvas, m, skin, text, centerX, centerY, plateR);
            }
            else
            {
                DrawDecorRings(canvas, m, skin, rim, text, centerX, centerY, plateR);
                DrawCenterDisc(canvas, m, skin, plate, rim, text, centerX, centerY);
                if (skin.ShowCenterTicks) DrawCenterTicks(canvas, m, skin, text, centerX, centerY);
            }
        }

        private static void DrawGlow(Canvas canvas, WheelMetrics m, WheelSkin skin, Color rim,
            double cx, double cy, double diameter, bool showGlow)
        {
            if (!showGlow || skin.GlowStrength <= 0) return;

            for (int layer = 1; layer <= 2; layer++)
            {
                double spread = m.GlowPadding * 0.62 * layer;
                double size = diameter + spread * 2;
                byte peak = (byte)Math.Round(34 * skin.GlowStrength / layer);

                Place(canvas, new Ellipse
                {
                    Width = size,
                    Height = size,
                    IsHitTestVisible = false,
                    Fill = new RadialGradientBrush
                    {
                        GradientStops = new GradientStopCollection
                        {
                            new(WheelSkinCatalog.WithAlpha(rim, 0), 0.60),
                            new(WheelSkinCatalog.WithAlpha(rim, peak), 0.79),
                            new(WheelSkinCatalog.WithAlpha(rim, 0), 1.0),
                        },
                    },
                }, cx - size / 2, cy - size / 2);
            }
        }

        private static void DrawDisc(Canvas canvas, WheelMetrics m, WheelSkin skin, Color plate,
            Color rim, double cx, double cy, double diameter)
        {
            double r = diameter / 2;

            // 环形布局的底盘中间不能被压暗 —— 中间是大片空区，
            // 压暗会把它读成一个"大黑圆盘"，正是之前出错的地方。
            var stops = skin.RingLayout
                ? new GradientStopCollection
                {
                    new(plate, 0.0),
                    new(plate, 0.62),
                    new(WheelSkinCatalog.Darken(plate, 0.22), 1.0),
                }
                : new GradientStopCollection
                {
                    new(WheelSkinCatalog.Darken(plate, skin.PlateVignette), 0.0),
                    new(plate, 0.62),
                    new(WheelSkinCatalog.Lighten(plate, 0.10), 1.0),
                };

            Place(canvas, new Ellipse
            {
                Width = diameter,
                Height = diameter,
                IsHitTestVisible = false,
                Fill = new RadialGradientBrush { GradientStops = stops },
            }, cx - r, cy - r);

            // 外缘描边：环形布局里这圈很淡，只是一道收边
            var rimColor = skin.RingLayout
                ? WheelSkinCatalog.Fade(rim, skin.RimAlpha * 0.55)
                : WheelSkinCatalog.Fade(rim, skin.RimAlpha);

            Place(canvas, new Ellipse
            {
                Width = diameter,
                Height = diameter,
                IsHitTestVisible = false,
                Stroke = new SolidColorBrush(rimColor),
                StrokeThickness = Math.Max(1.0, m.BorderThickness),
                Fill = null,
            }, cx - r, cy - r);
        }

        /// <summary>中间那圈浅色圆环（空区与外环带之间的过渡）。</summary>
        private static void DrawDonut(Canvas canvas, WheelMetrics m, WheelSkin skin, Color text,
            double cx, double cy, double plateR)
        {
            double inner = plateR * WheelGeometry.RingDonutInnerRatio;
            double outer = plateR * WheelGeometry.RingDonutOuterRatio;

            AddAnnulus(canvas, cx, cy, inner, outer,
                WheelSkinCatalog.WithAlpha(text, (byte)Math.Round(255 * 0.055)));

            var edge = new SolidColorBrush(
                WheelSkinCatalog.WithAlpha(text, (byte)Math.Round(255 * (skin.InnerRingAlpha * 0.62))));
            AddRing(canvas, cx, cy, inner, edge.Color, 1.0);
            AddRing(canvas, cx, cy, outer, edge.Color, 1.0);
        }

        /// <summary>
        /// 外侧图标环带：很淡的底 + 边界细线 + 把环带切成扇区的径向线 + 序号。
        /// </summary>
        private static void DrawRingBand(Canvas canvas, WheelMetrics m, WheelSkin skin, Color text,
            double cx, double cy, double plateR)
        {
            double inner = plateR * WheelGeometry.RingBandInnerRatio;
            double outer = plateR * WheelGeometry.RingBandOuterRatio;

            AddAnnulus(canvas, cx, cy, inner, outer,
                WheelSkinCatalog.WithAlpha(text, (byte)Math.Round(255 * 0.022)));

            AddRing(canvas, cx, cy, inner,
                WheelSkinCatalog.WithAlpha(text, (byte)Math.Round(255 * (skin.InnerRingAlpha * 0.55))), 1.0);

            int count = Math.Max(1, m.DirectionCount);
            double step = 360.0 / count;

            // 分隔线画在两槽位中间，并从中心环一直延伸到外缘（和参考图一致）
            double spokeInner = plateR * WheelGeometry.RingDonutInnerRatio;
            var divider = new SolidColorBrush(
                WheelSkinCatalog.WithAlpha(text, (byte)Math.Round(255 * skin.SpokeAlpha)));

            for (int i = 0; i < count; i++)
            {
                double angle = -90.0 + step * (i + 0.5);
                double rad = angle * Math.PI / 180.0;
                double cos = Math.Cos(rad), sin = Math.Sin(rad);

                canvas.Children.Add(new Line
                {
                    X1 = cx + spokeInner * cos,
                    Y1 = cy + spokeInner * sin,
                    X2 = cx + outer * cos,
                    Y2 = cy + outer * sin,
                    Stroke = divider,
                    StrokeThickness = 1,
                    IsHitTestVisible = false,
                });
            }

            // 序号：紧贴图标的顺时针一侧，和参考图一样"贴在图标边上"
            double badgeR = plateR * WheelGeometry.RingBadgeRadiusRatio;
            double badgeSize = Math.Max(9, plateR * 0.070);
            var badgeFill = new SolidColorBrush(
                WheelSkinCatalog.WithAlpha(text, (byte)Math.Round(255 * 0.95)));

            for (int i = 0; i < count; i++)
            {
                double angle = -90.0 + step * i + step * 0.175;
                double rad = angle * Math.PI / 180.0;

                var label = new TextBlock
                {
                    Text = (i + 1).ToString(),
                    FontSize = badgeSize,
                    FontWeight = FontWeights.Bold,
                    Foreground = badgeFill,
                    IsHitTestVisible = false,
                    Effect = new System.Windows.Media.Effects.DropShadowEffect
                    {
                        Color = Colors.Black,
                        ShadowDepth = 0,
                        BlurRadius = 4,
                        Opacity = 0.9,
                    },
                };
                label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

                Place(canvas, label,
                    cx + badgeR * Math.Cos(rad) - label.DesiredSize.Width / 2,
                    cy + badgeR * Math.Sin(rad) - label.DesiredSize.Height / 2);
            }
        }

        /// <summary>浮动圆钮布局的装饰：双环 + 辐条（对准槽位）。</summary>
        private static void DrawDecorRings(Canvas canvas, WheelMetrics m, WheelSkin skin, Color rim,
            Color text, double cx, double cy, double plateR)
        {
            if (skin.ShowInnerRing)
            {
                AddRing(canvas, cx, cy, plateR * 0.965,
                    WheelSkinCatalog.Fade(rim, skin.InnerRingAlpha * 0.9), 1.0);
                AddRing(canvas, cx, cy, plateR * 0.800,
                    WheelSkinCatalog.Fade(rim, skin.InnerRingAlpha), 1.0);
            }

            if (!skin.ShowSpokes || skin.SpokeAlpha <= 0) return;

            int count = Math.Max(1, m.DirectionCount);
            double innerR = Math.Max(m.InnerDiameter / 2 * 1.04, plateR * 0.26);
            var spokeBrush = new SolidColorBrush(
                WheelSkinCatalog.WithAlpha(text, (byte)Math.Round(255 * skin.SpokeAlpha)));

            for (int i = 0; i < count; i++)
            {
                double angle = (-90.0 + 360.0 / count * i) * Math.PI / 180.0;
                double cos = Math.Cos(angle), sin = Math.Sin(angle);

                canvas.Children.Add(new Line
                {
                    X1 = cx + innerR * cos,
                    Y1 = cy + innerR * sin,
                    X2 = cx + plateR * 0.992 * cos,
                    Y2 = cy + plateR * 0.992 * sin,
                    Stroke = spokeBrush,
                    StrokeThickness = 1,
                    IsHitTestVisible = false,
                });
            }
        }

        private static void DrawCenterDisc(Canvas canvas, WheelMetrics m, WheelSkin skin, Color plate,
            Color rim, Color text, double cx, double cy)
        {
            double diameter = m.InnerDiameter;
            double r = diameter / 2;

            Place(canvas, new Ellipse
            {
                Width = diameter,
                Height = diameter,
                IsHitTestVisible = false,
                Fill = new RadialGradientBrush
                {
                    GradientStops = new GradientStopCollection
                    {
                        new(WheelSkinCatalog.Darken(plate, 0.34), 0.0),
                        new(WheelSkinCatalog.Darken(plate, 0.16), 0.74),
                        new(WheelSkinCatalog.Fade(rim, 0.12), 1.0),
                    },
                },
                Stroke = new SolidColorBrush(WheelSkinCatalog.Fade(rim, skin.InnerRingAlpha + 0.20)),
                StrokeThickness = 1.2,
            }, cx - r, cy - r);

            AddRing(canvas, cx, cy, r * 0.78, WheelSkinCatalog.Fade(rim, 0.10), 1.0);
        }

        private static void DrawCenterTicks(Canvas canvas, WheelMetrics m, WheelSkin skin, Color text,
            double cx, double cy)
        {
            double innerR = m.InnerDiameter / 2;
            double r1 = innerR * 1.10;
            double r2 = innerR * 1.24;
            var brush = new SolidColorBrush(
                WheelSkinCatalog.WithAlpha(text, (byte)Math.Round(255 * skin.InnerRingAlpha * 0.5)));

            for (int i = 0; i < 24; i++)
            {
                double angle = 360.0 / 24 * i * Math.PI / 180.0;
                double cos = Math.Cos(angle), sin = Math.Sin(angle);
                bool major = i % 3 == 0;

                canvas.Children.Add(new Line
                {
                    X1 = cx + r1 * cos,
                    Y1 = cy + r1 * sin,
                    X2 = cx + (major ? r2 : r1 + (r2 - r1) * 0.55) * cos,
                    Y2 = cy + (major ? r2 : r1 + (r2 - r1) * 0.55) * sin,
                    Stroke = brush,
                    StrokeThickness = major ? 1.2 : 1.0,
                    IsHitTestVisible = false,
                });
            }
        }

        // ============================ 选中扇区 ============================

        /// <summary>环形布局下高亮选中的扇区。索引 -1 表示清掉。</summary>
        public static void DrawSelection(
            Canvas canvas,
            WheelMetrics m,
            WheelSkin skin,
            Color accent,
            Color text,
            double centerX,
            double centerY,
            int index)
        {
            const string Tag = "selection-wedge";
            Shape? existing = null;

            foreach (var child in canvas.Children)
            {
                if (child is Shape s && Equals(s.Tag, Tag)) { existing = s; break; }
            }

            if (index < 0)
            {
                if (existing != null) canvas.Children.Remove(existing);
                return;
            }

            double plateR = m.PlateRadius;
            int count = Math.Max(1, m.DirectionCount);
            double step = 360.0 / count;
            double center = -90.0 + step * index;

            var geo = AnnularSector(centerX, centerY,
                plateR * WheelGeometry.RingBandInnerRatio,
                plateR * WheelGeometry.RingBandOuterRatio,
                center - step / 2 + 0.9, center + step / 2 - 0.9);

            var fill = new SolidColorBrush(WheelSkinCatalog.Fade(accent, 0.26));
            var stroke = new SolidColorBrush(WheelSkinCatalog.Fade(accent, 0.75));

            if (existing is Path path)
            {
                path.Data = geo;
                path.Fill = fill;
                path.Stroke = stroke;
                return;
            }

            canvas.Children.Add(new Path
            {
                Data = geo,
                Fill = fill,
                Stroke = stroke,
                StrokeThickness = 1.2,
                IsHitTestVisible = false,
                Tag = Tag,
            });
        }

        private static Geometry AnnularSector(double cx, double cy,
            double rIn, double rOut, double startDeg, double endDeg)
        {
            Point p1 = Polar(cx, cy, rOut, startDeg);
            Point p2 = Polar(cx, cy, rOut, endDeg);
            Point p3 = Polar(cx, cy, rIn, endDeg);
            Point p4 = Polar(cx, cy, rIn, startDeg);

            var figure = new PathFigure { StartPoint = p1, IsClosed = true, IsFilled = true };
            figure.Segments.Add(new ArcSegment(p2, new Size(rOut, rOut), 0, false,
                SweepDirection.Clockwise, true));
            figure.Segments.Add(new LineSegment(p3, true));
            figure.Segments.Add(new ArcSegment(p4, new Size(rIn, rIn), 0, false,
                SweepDirection.Counterclockwise, true));

            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            geometry.Freeze();
            return geometry;
        }

        private static PathFigure CircleFigure(double cx, double cy, double r)
        {
            var figure = new PathFigure
            {
                StartPoint = new Point(cx + r, cy),
                IsClosed = true,
                IsFilled = true,
            };
            figure.Segments.Add(new ArcSegment(new Point(cx - r, cy), new Size(r, r), 0, false,
                SweepDirection.Clockwise, true));
            figure.Segments.Add(new ArcSegment(new Point(cx + r, cy), new Size(r, r), 0, false,
                SweepDirection.Clockwise, true));
            return figure;
        }

        private static Point Polar(double cx, double cy, double r, double degrees)
        {
            double rad = degrees * Math.PI / 180.0;
            return new Point(cx + r * Math.Cos(rad), cy + r * Math.Sin(rad));
        }

        // ============================ 槽位 ============================

        /// <summary>浮动圆钮布局的圆底画刷（选中/未选中各一份）。</summary>
        public static Brush SlotBrush(Color baseColor, WheelSkin skin, bool selected)
        {
            double strength = Math.Clamp(skin.SlotGradient, 0, 1);
            Color top = WheelSkinCatalog.Lighten(baseColor, 0.10 + strength * 0.26);
            Color bottom = WheelSkinCatalog.Darken(baseColor, 0.08 + strength * 0.34);

            if (selected)
            {
                top = WheelSkinCatalog.Lighten(top, 0.10);
                bottom = WheelSkinCatalog.Darken(bottom, 0.10);
            }

            var brush = new LinearGradientBrush
            {
                StartPoint = new Point(0.5, 0),
                EndPoint = new Point(0.5, 1),
                GradientStops = new GradientStopCollection
                {
                    new(top, 0.0),
                    new(baseColor, 0.48),
                    new(bottom, 1.0),
                },
            };
            brush.Freeze();
            return brush;
        }

        /// <summary>
        /// 槽位内容。
        /// 环形布局：只有图标，没有圆底、没有标签 —— 名字只在中心显示，
        ///   这样图标能稳稳落在扇区中线上（带标签的纵向堆叠会把图标顶偏）。
        /// 浮动布局：图标 + 名字 + 顶部高光 + 边缘高光。
        /// </summary>
        public static FrameworkElement BuildSlotContent(
            WheelItem? item, double itemSize, WheelSkin skin, Color textColor, bool selected)
        {
            if (skin.RingLayout)
                return BuildRingSlotContent(item, itemSize, textColor, selected);

            return BuildChippedSlotContent(item, itemSize, skin, textColor, selected);
        }

        /// <summary>环形布局的槽位：一个居中的图标，仅此而已。</summary>
        private static FrameworkElement BuildRingSlotContent(
            WheelItem? item, double itemSize, Color textColor, bool selected)
        {
            var host = new Grid
            {
                Width = itemSize,
                Height = itemSize,
                IsHitTestVisible = false,
            };

            // 图标占环带宽度的大约四分之三：填满整条环带会显得拥挤
            double iconSize = itemSize * 0.72;
            var image = TryResolveIcon(item, iconSize);

            FrameworkElement visual;
            if (image != null)
            {
                visual = new Image
                {
                    Source = image,
                    Width = iconSize,
                    Height = iconSize,
                    Stretch = Stretch.Uniform,
                    Opacity = selected ? 1.0 : 0.95,
                };
            }
            else
            {
                visual = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(item?.Icon) ? "＋" : item!.Icon,
                    FontSize = Math.Max(15, itemSize * 0.66),
                    Foreground = new SolidColorBrush(
                        WheelSkinCatalog.WithAlpha(textColor, selected ? (byte)255 : (byte)215)),
                    TextAlignment = TextAlignment.Center,
                };
            }

            visual.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 6,
                ShadowDepth = 0,
                Opacity = 0.55,
            };

            // 居中 —— 不加 Label，避免把图标顶偏
            visual.HorizontalAlignment = HorizontalAlignment.Center;
            visual.VerticalAlignment = VerticalAlignment.Center;
            host.Children.Add(visual);
            return host;
        }

        private static FrameworkElement BuildChippedSlotContent(
            WheelItem? item, double itemSize, WheelSkin skin, Color textColor, bool selected)
        {
            double pad = Math.Max(1.0, itemSize * 0.035);

            var grid = new Grid
            {
                Width = itemSize,
                Height = itemSize,
                IsHitTestVisible = false,
            };

            double radius = itemSize / 2;
            grid.Clip = new EllipseGeometry(new Point(radius, radius), radius, radius);

            grid.Children.Add(new Ellipse
            {
                IsHitTestVisible = false,
                Margin = new Thickness(itemSize * 0.10, itemSize * 0.05, itemSize * 0.10, itemSize * 0.52),
                Fill = new LinearGradientBrush
                {
                    StartPoint = new Point(0.5, 0),
                    EndPoint = new Point(0.5, 1),
                    GradientStops = new GradientStopCollection
                    {
                        new(Color.FromArgb(selected ? (byte)70 : (byte)34, 255, 255, 255), 0.0),
                        new(Color.FromArgb(0, 255, 255, 255), 1.0),
                    },
                },
            });

            grid.Children.Add(new Ellipse
            {
                IsHitTestVisible = false,
                Margin = new Thickness(pad, pad, pad, pad),
                Stroke = new SolidColorBrush(WheelSkinCatalog.WithAlpha(
                    textColor, (byte)Math.Round(255 * (skin.SlotRimAlpha + (selected ? 0.30 : 0.0))))),
                StrokeThickness = 1,
                Fill = null,
            });

            grid.Children.Add(BuildLabeledSlotContent(item, itemSize, textColor, selected));
            return grid;
        }

        private static FrameworkElement BuildLabeledSlotContent(
            WheelItem? item, double itemSize, Color textColor, bool selected)
        {
            var stack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            };

            var image = TryResolveIcon(item, itemSize);

            if (image != null)
            {
                stack.Children.Add(new Image
                {
                    Source = image,
                    Width = itemSize * WheelGeometry.IconImageFactor,
                    Height = itemSize * WheelGeometry.IconImageFactor,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Center,
                });
            }
            else
            {
                stack.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(item?.Icon) ? "●" : item!.Icon,
                    FontSize = Math.Max(12, itemSize * WheelGeometry.IconFontFactor),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                    Foreground = new SolidColorBrush(textColor),
                });
            }

            if (item != null && !string.IsNullOrWhiteSpace(item.Name))
            {
                var label = new TextBlock
                {
                    Text = item.Name,
                    FontSize = Math.Max(8, itemSize * WheelGeometry.LabelFontFactor),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = itemSize * WheelGeometry.LabelMaxWidthFactor,
                    Foreground = new SolidColorBrush(textColor),
                    Margin = new Thickness(0, 1, 0, 0),
                };

                label.Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Colors.Black,
                    BlurRadius = 3,
                    ShadowDepth = 0,
                    Opacity = 0.55,
                };

                stack.Children.Add(label);
            }

            return stack;
        }

        // ============================ 图标 ============================

        /// <summary>
        /// 图标解析优先级：
        ///   IconPath == "genshin:slug" / "genshin:中文名" → 内置原神图标包
        ///   IconPath == "auto"                             → 从目标程序/文件夹提取
        ///   IconPath 是真实文件                             → 加载该图片
        ///   否则                                           → emoji（返回 null）
        /// </summary>
        public static ImageSource? TryResolveIcon(WheelItem? item, double itemSize)
        {
            if (item == null) return null;

            try
            {
                var iconPath = item.IconPath?.Trim() ?? "";
                double decodeWidth = Math.Max(
                    WheelGeometry.MinIconDecodeWidth, itemSize * WheelGeometry.IconDecodeFactor);

                if (iconPath.StartsWith("genshin:", StringComparison.OrdinalIgnoreCase))
                {
                    return GenshinIcons.Load(iconPath.Substring("genshin:".Length), decodeWidth);
                }

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
                    bitmap.DecodePixelWidth = (int)decodeWidth;
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

        // ============================ 小工具 ============================

        private static void Place(Canvas canvas, UIElement element, double left, double top)
        {
            Canvas.SetLeft(element, left);
            Canvas.SetTop(element, top);
            canvas.Children.Add(element);
        }

        private static void AddRing(Canvas canvas, double cx, double cy,
            double radius, Color color, double thickness)
        {
            Place(canvas, new Ellipse
            {
                Width = radius * 2,
                Height = radius * 2,
                Stroke = new SolidColorBrush(color),
                StrokeThickness = thickness,
                Fill = null,
                IsHitTestVisible = false,
            }, cx - radius, cy - radius);
        }

        private static void AddAnnulus(Canvas canvas, double cx, double cy,
            double inner, double outer, Color color)
        {
            var geometry = new PathGeometry { FillRule = FillRule.EvenOdd };
            geometry.Figures.Add(CircleFigure(cx, cy, outer));
            geometry.Figures.Add(CircleFigure(cx, cy, inner));
            geometry.Freeze();

            canvas.Children.Add(new Path
            {
                Data = geometry,
                Fill = new SolidColorBrush(color),
                IsHitTestVisible = false,
            });
        }
    }
}
