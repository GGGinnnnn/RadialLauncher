using RadialLauncher.Models;

namespace RadialLauncher.Services
{
    /// <summary>
    /// 轮盘的几何计算。纯函数、无 WPF 依赖，因此可以直接单元测试。<br/>
    /// <see cref="Views.WheelWindow"/>（真实显示）与 <see cref="Views.WheelPreview"/>（设置面板预览）
    /// 共用这里的计算，避免两处魔数各自漂移 —— 之前预览就因此把四向轮盘算小了一圈。
    /// </summary>
    public static class WheelGeometry
    {
        /// <summary>4 向布局时，圆心到选项中心的距离相对 itemSize 的系数。</summary>
        public const double RadiusFactor4 = 2.05;

        /// <summary>8 向布局时，圆心到选项中心的距离相对 itemSize 的系数。</summary>
        public const double RadiusFactor8 = 1.95;

        /// <summary>底盘半径相对 itemSize 的额外留白系数。</summary>
        public const double PlatePaddingFactor = 0.72;

        /// <summary>四向布局选项之间的角度间隔是 90°，需要更大的半径才能不显得挤。</summary>
        public const double DirectionCount4Step = 90.0;

        /// <summary>选项中图标的字号相对 itemSize 的比例。</summary>
        public const double IconFontFactor = 0.34;

        /// <summary>选项名的字号相对 itemSize 的比例。</summary>
        public const double LabelFontFactor = 0.128;

        /// <summary>图标图片边长相对 itemSize 的比例。</summary>
        public const double IconImageFactor = 0.44;

        /// <summary>选项名最大宽度相对 itemSize 的比例。</summary>
        public const double LabelMaxWidthFactor = 0.94;

        /// <summary>图标图片解码宽度的最小值（像素）。</summary>
        public const double MinIconDecodeWidth = 16.0;

        /// <summary>图标图片解码宽度相对 itemSize 的比例。</summary>
        public const double IconDecodeFactor = 0.6;

        /// <summary>中心圆盘直径相对底盘直径的比例。</summary>
        public const double InnerRatio = 0.34;

        // ---------- 环形布局（默认 2）的比例，全部相对底盘半径 ----------

        /// <summary>图标所在半径：0.82 —— 正好落在外侧环带的中线上。</summary>
        public const double RingSlotRadiusRatio = 0.822;

        /// <summary>外侧环带（图标带）的外缘。</summary>
        public const double RingBandOuterRatio = 0.984;

        /// <summary>外侧环带（图标带）的内缘。</summary>
        public const double RingBandInnerRatio = 0.665;

        /// <summary>中间那圈浅色圆环的外缘。</summary>
        public const double RingDonutOuterRatio = 0.660;

        /// <summary>中间那圈浅色圆环的内缘 —— 再往里就是一大片空区。</summary>
        public const double RingDonutInnerRatio = 0.498;

        /// <summary>序号所在半径。</summary>
        public const double RingBadgeRadiusRatio = 0.908;

        /// <summary>底盘描边粗细（显示光晕时加粗一点）。</summary>
        public const double BorderThicknessNormal = 2.0;
        public const double BorderThicknessGlow = 2.5;

        /// <summary>光晕预留的额外内边距（DIP，不随 scale 变化）。</summary>
        public const double GlowPadding = 36.0;

        /// <summary>选项最小尺寸，防止预览区过小导致算出 0 或负数。</summary>
        public const double MinItemSize = 14.0;

        /// <summary>
        /// 把方向数收敛到受支持的 4 或 8。
        /// </summary>
        public static int NormalizeDirectionCount(int directionCount)
            => directionCount is 4 or 8 ? directionCount : 8;

        /// <summary>
        /// 圆心到选项中心的标准距离（不含任何外部缩放）。
        /// </summary>
        public static double RadiusFor(double itemSize, int directionCount)
            => itemSize * (NormalizeDirectionCount(directionCount) == 4 ? RadiusFactor4 : RadiusFactor8);

        /// <summary>
        /// 给定外观设置与几何缩放系数，算出全部尺寸。
        /// </summary>
        /// <param name="appearance">外观设置（不应为 null，调用方负责兜底）。</param>
        /// <param name="directionCount">方向数，非 4/8 时按 8 处理。</param>
        /// <param name="geometryScale">
        /// 额外的整体缩放：真实显示传 1.0；预览按可用区域算出的 fit 系数。
        /// </param>
        public static WheelMetrics Compute(
            AppearanceSettings appearance, int directionCount, double geometryScale = 1.0)
        {
            double scale = appearance.Scale * geometryScale;
            // itemSize 保底，避免极端配置或极小预览区算出 0 / 负数尺寸。
            // 保底后 radius / plateRadius / windowSize 全部基于同一个 itemSize，保证几何自洽。
            double itemSize = System.Math.Max(MinItemSize, appearance.ItemSize * scale);

            int dirs = NormalizeDirectionCount(directionCount);
            double radius = RadiusFor(itemSize, dirs);
            double plateRadius = radius + itemSize * PlatePaddingFactor;
            double glowPadding = appearance.ShowGlow ? GlowPadding : 0.0;
            double windowSize = 2 * (plateRadius + glowPadding);
            double plateDiameter = plateRadius * 2;

            return new WheelMetrics
            {
                DirectionCount = dirs,
                Scale = scale,
                ItemSize = itemSize,
                Radius = radius,
                PlateRadius = plateRadius,
                PlateDiameter = plateDiameter,
                WindowSize = windowSize,
                GlowPadding = glowPadding,
                InnerDiameter = plateDiameter * InnerRatio,
                RingSlotRadius = plateRadius * RingSlotRadiusRatio,
                BorderThickness = appearance.ShowGlow ? BorderThicknessGlow : BorderThicknessNormal,
                DeadZone = System.Math.Max(12, appearance.DeadZone * scale),
                IconFontSize = System.Math.Max(12, itemSize * IconFontFactor),
                LabelFontSize = System.Math.Max(8, itemSize * LabelFontFactor),
                IconImageSize = itemSize * IconImageFactor,
                LabelMaxWidth = itemSize * LabelMaxWidthFactor,
                IconDecodeWidth = System.Math.Max(MinIconDecodeWidth, itemSize * IconDecodeFactor),
            };
        }

        /// <summary>
        /// 预览用的整体缩放系数：让轮盘刚好塞进 <paramref name="availableSize"/> 的正方形区域。
        ///
        /// 注意 windowSize 对 geometryScale <b>不是线性的</b>：
        /// 随缩放变化的是 itemSize 与 plateRadius，而光晕留白（<see cref="GlowPadding"/>）
        /// 是固定的视觉外扩、不参与缩放。因此不能简单用 usable / baseWindow，
        /// 必须先把光晕的 2*glow 从可用宽度里扣掉，再解缩放系数。
        ///
        /// 这里刻意用 <paramref name="directionCount"/> 参与计算 —— 四向轮盘的窗口更大，
        /// 之前预览固定按八向估算，导致四向轮盘被裁掉一圈。
        /// </summary>
        public static double FitScaleFor(
            AppearanceSettings appearance, int directionCount, double availableSize, double margin = 16.0)
        {
            double usable = availableSize - margin;
            if (usable <= 0) return 1.0;

            var dirs = NormalizeDirectionCount(directionCount);
            double radiusFactor = dirs == 4 ? RadiusFactor4 : RadiusFactor8;

            // 先扣掉不缩放的光晕外框（左右两侧各 glow，所以减 2*glow）
            double glowPadding = appearance.ShowGlow ? GlowPadding : 0.0;
            double scalableUsable = usable - 2 * glowPadding;
            if (scalableUsable <= 0) return 1.0;

            // 随 scale 线性变化的部分：2 * (radius + platePadding)
            // 与 Compute 一样对 itemSize 保底，否则算出偏小的 fit 会让结果溢出。
            double effectiveItemSize = System.Math.Max(
                MinItemSize, appearance.ItemSize * appearance.Scale);
            double scalableBase = 2 * effectiveItemSize * (radiusFactor + PlatePaddingFactor);
            if (scalableBase <= 0) return 1.0;

            return System.Math.Min(1.0, scalableUsable / scalableBase);
        }
    }

    /// <summary>一次算好的轮盘尺寸集合。</summary>
    public sealed class WheelMetrics
    {
        public int DirectionCount { get; init; }
        public double Scale { get; init; }
        public double ItemSize { get; init; }
        public double Radius { get; init; }
        public double PlateRadius { get; init; }
        public double PlateDiameter { get; init; }
        public double WindowSize { get; init; }
        public double GlowPadding { get; init; }
        public double InnerDiameter { get; init; }

        /// <summary>环形布局下圆心到图标中心的距离。</summary>
        public double RingSlotRadius { get; init; }
        public double BorderThickness { get; init; }
        public double DeadZone { get; init; }
        public double IconFontSize { get; init; }
        public double LabelFontSize { get; init; }
        public double IconImageSize { get; init; }
        public double LabelMaxWidth { get; init; }
        public double IconDecodeWidth { get; init; }
    }
}
