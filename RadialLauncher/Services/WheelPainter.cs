using System;
using System.Collections.Generic;
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
    ///   <item><b>环形（原神皮肤）</b>：一整块深色底盘 + 一圈"环带"，
    ///   环带被 n 条极细的径向线切成扇区，图标直接浮在环带上，
    ///   扇区外缘还有个小序号；选中时高亮整个扇区（扇形楔块），而不是把圆点换色。
    ///   这就是游戏里那个样子。</item>
    ///   <item><b>浮动圆钮（默认皮肤）</b>：每个槽位是一枚带渐变的圆钮，
    ///   选中时整枚换成高亮渐变。</item>
    /// </list>
    /// </summary>
    public static class WheelPainter
    {
        /// <summary>环带外缘相对底盘半径的比例。</summary>
        public const double RingOuterRatio = 0.962;

        /// <summary>环带内缘相对底盘半径的比例。</summary>
        public const double RingInnerRatio = 0.515;

        /// <summary>序号所在半径（相对底盘半径）。</summary>
        public const double BadgeRadiusRatio = 0.888;

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

            if (skin.RingLayout) DrawRingBand(canvas, m, skin, rim, text, centerX, centerY, plateR);
            else DrawDecorRings(canvas, m, skin, rim, text, centerX, centerY, plateR);

            DrawCenterDisc(canvas, m, skin, plate, rim, text, centerX, centerY);
            if (skin.ShowCenterTicks) DrawCenterTicks(canvas, m, skin, text, centerX, centerY);
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

                var halo = new Ellipse
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
                };
                Place(canvas, halo, cx - size / 2, cy - size / 2);
            }
        }

        private static void DrawDisc(Canvas canvas, WheelMetrics m, WheelSkin skin, Color plate,
            Color rim, double cx, double cy, double diameter)
        {
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
                        new(WheelSkinCatalog.Darken(plate, skin.PlateVignette), 0.0),
                        new(plate, 0.62),
                        new(WheelSkinCatalog.Lighten(plate, 0.10), 1.0),
                    },
                },
            }, cx - r, cy - r);

            Place(canvas, new Ellipse
            {
                Width = diameter,
                Height = diameter,
                IsHitTestVisible = false,
                Stroke = new SolidColorBrush(WheelSkinCatalog.Fade(rim, skin.RimAlpha)),
                StrokeThickness = Math.Max(1.0, m.BorderThickness),
                Fill = null,
            }, cx - r, cy - r);
        }

        /// <summary>
        /// 环形布局：环带 + 扇区分隔线 + 序号。
        /// 环带本身只是很淡的一层提亮，主要靠两条边界细线把"带"框出来。
        /// </summary>
        private static void DrawRingBand(Canvas canvas, WheelMetrics m, WheelSkin skin, Color rim,
            Color text, double cx, double cy, double plateR)
        {
            double outer = plateR * RingOuterRatio;
            double inner = plateR * RingInnerRatio;

            // 环带底色：两个同心圆 + 偶数填充规则做出圆环（不要用"再盖一个圆"，
            // 底盘是半透明的，叠一层会把内圈压得比外圈还暗）
            var annulus = new PathGeometry { FillRule = FillRule.EvenOdd };
            annulus.Figures.Add(CircleFigure(cx, cy, outer));
            annulus.Figures.Add(CircleFigure(cx, cy, inner));
            annulus.Freeze();

            canvas.Children.Add(new Path
            {
                Data = annulus,
                Fill = new SolidColorBrush(WheelSkinCatalog.WithAlpha(text, 12)),
                IsHitTestVisible = false,
            });

            // 两条边界细线
            var edge = new SolidColorBrush(WheelSkinCatalog.WithAlpha(text,
                (byte)Math.Round(255 * (skin.InnerRingAlpha + 0.12))));
            AddRing(canvas, cx, cy, outer, edge.Color, 1.0);
            AddRing(canvas, cx, cy, inner, edge.Color, 1.0);

            // 扇区分隔线：画在"两个槽位中间"，把环带切成 n 段
            int count = Math.Max(1, m.DirectionCount);
            double step = 360.0 / count;
            var divider = new SolidColorBrush(WheelSkinCatalog.WithAlpha(text,
                (byte)Math.Round(255 * (skin.SpokeAlpha + 0.06))));

            for (int i = 0; i < count; i++)
            {
                double angle = -90.0 + step * (i + 0.5);
                double rad = angle * Math.PI / 180.0;
                double cos = Math.Cos(rad), sin = Math.Sin(rad);

                canvas.Children.Add(new Line
                {
                    X1 = cx + inner * cos,
                    Y1 = cy + inner * sin,
                    X2 = cx + outer * cos,
                    Y2 = cy + outer * sin,
                    Stroke = divider,
                    StrokeThickness = 1,
                    IsHitTestVisible = false,
                });
            }

            // 序号：贴在扇区外缘、稍微偏离图标一点，避免和名字打架
            double badgeR = plateR * BadgeRadiusRatio;
            double badgeSize = Math.Max(8, plateR * 0.062);
            var badgeBrush = new SolidColorBrush(WheelSkinCatalog.WithAlpha(text, (byte)138));

            for (int i = 0; i < count; i++)
            {
                double angle = -90.0 + step * (i + 0.30);
                double rad = angle * Math.PI / 180.0;

                var label = new TextBlock
                {
                    Text = (i + 1).ToString(),
                    FontSize = badgeSize,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = badgeBrush,
                    IsHitTestVisible = false,
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

        /// <summary>
        /// 环形布局下高亮选中的扇区（扇形楔块）。
        /// 索引 -1 表示清掉高亮。
        /// </summary>
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
            // 复用同一个高亮层：先找到它，没有就建一个
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
                plateR * RingInnerRatio, plateR * RingOuterRatio,
                center - step / 2 + 0.6, center + step / 2 - 0.6);

            if (existing is Path path)
            {
                path.Data = geo;
                path.Fill = new SolidColorBrush(WheelSkinCatalog.Fade(accent, 0.34));
                path.Stroke = new SolidColorBrush(WheelSkinCatalog.Fade(accent, 0.85));
                return;
            }

            var wedge = new Path
            {
                Data = geo,
                Fill = new SolidColorBrush(WheelSkinCatalog.Fade(accent, 0.34)),
                Stroke = new SolidColorBrush(WheelSkinCatalog.Fade(accent, 0.85)),
                StrokeThickness = 1.2,
                IsHitTestVisible = false,
                Tag = Tag,
            };

            canvas.Children.Add(wedge);
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
        /// 环形布局：只有图标 + 名字（没有圆底、没有高光边框）——
        ///   圆底一加上去就变成"一串浮动圆钮"，和游戏里那个环带完全不是一个东西。
        /// 浮动布局：图标 + 名字 + 顶部高光 + 边缘高光。
        /// </summary>
        public static FrameworkElement BuildSlotContent(
            WheelItem? item, double itemSize, WheelSkin skin, Color textColor, bool selected)
        {
            if (skin.RingLayout)
                return BuildFlatSlotContent(item, itemSize, textColor, selected);

            return BuildChippedSlotContent(item, itemSize, skin, textColor, selected);
        }

        private static FrameworkElement BuildFlatSlotContent(
            WheelItem? item, double itemSize, Color textColor, bool selected)
        {
            var stack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            };

            double iconSize = itemSize * 0.62;
            var image = TryResolveIcon(item, itemSize);

            if (image != null)
            {
                stack.Children.Add(new Image
                {
                    Source = image,
                    Width = iconSize,
                    Height = iconSize,
                    Stretch = Stretch.Uniform,
                    Opacity = selected ? 1.0 : 0.92,
                    Effect = new System.Windows.Media.Effects.DropShadowEffect
                    {
                        Color = Colors.Black,
                        BlurRadius = 6,
                        ShadowDepth = 0,
                        Opacity = 0.5,
                    },
                });
            }
            else
            {
                stack.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(item?.Icon) ? "＋" : item!.Icon,
                    FontSize = Math.Max(14, itemSize * 0.50),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Foreground = new SolidColorBrush(
                        WheelSkinCatalog.WithAlpha(textColor, selected ? (byte)255 : (byte)200)),
                });
            }

            if (item != null && !string.IsNullOrWhiteSpace(item.Name))
            {
                stack.Children.Add(new TextBlock
                {
                    Text = item.Name,
                    FontSize = Math.Max(8, itemSize * 0.145),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = itemSize * 1.15,
                    Foreground = new SolidColorBrush(
                        WheelSkinCatalog.WithAlpha(textColor, selected ? (byte)255 : (byte)205)),
                    Margin = new Thickness(0, 2, 0, 0),
                    Effect = new System.Windows.Media.Effects.DropShadowEffect
                    {
                        Color = Colors.Black,
                        BlurRadius = 4,
                        ShadowDepth = 0,
                        Opacity = 0.65,
                    },
                });
            }

            return stack;
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

            grid.Children.Add(BuildFlatSlotContent(item, itemSize, textColor, selected));
            return grid;
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
    }
}
