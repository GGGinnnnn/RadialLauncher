using System.Collections.Generic;
using System.Windows.Media;

namespace RadialLauncher.Services
{
    /// <summary>
    /// 轮盘皮肤：只描述「装饰风格」，不接管颜色。
    ///
    /// <para>颜色仍然由 <see cref="Models.AppearanceSettings"/> 里那五个字段决定，
    /// 皮肤只决定这些装饰参数 —— 这样用户换皮肤之后还能继续微调配色，
    /// 而不是被皮肤锁死。切换皮肤时设置面板会把对应预设配色填进那五个字段。</para>
    /// </summary>
    public sealed record WheelSkin(
        string Id,
        string Zh,
        string En,

        /// <summary>是否画从中心射向外缘的辐条。</summary>
        bool ShowSpokes,

        /// <summary>是否在中心盘外围画一圈短刻度。</summary>
        bool ShowCenterTicks,

        /// <summary>是否画内外双环。</summary>
        bool ShowInnerRing,

        /// <summary>外圈描边的不透明度（会乘到边框色自身的 alpha 上）。</summary>
        double RimAlpha,

        /// <summary>内环的不透明度。</summary>
        double InnerRingAlpha,

        /// <summary>辐条的不透明度。</summary>
        double SpokeAlpha,

        /// <summary>槽位自上而下的渐变强度（0 = 纯色，1 = 明显立体）。</summary>
        double SlotGradient,

        /// <summary>槽位边缘高光的不透明度。</summary>
        double SlotRimAlpha,

        /// <summary>底盘中心压暗程度（做出"碗"的纵深感）。</summary>
        double PlateVignette,

        /// <summary>外发光的强度系数。</summary>
        double GlowStrength,

        /// <summary>
        /// true = 环形布局（整块底盘 + 一圈被径向线切开的环带，图标浮在环带上，
        /// 选中时高亮整个扇区）；false = 浮动圆钮布局。
        /// </summary>
        bool RingLayout = false);

    /// <summary>皮肤的注册表与预设配色。</summary>
    public static class WheelSkinCatalog
    {
        public const string DefaultId = "default";
        public const string RingId = "ring";

        /// <summary>旧配置里这个皮肤曾经叫 genshin，读到时映射回来。</summary>
        private const string RingLegacyId = "genshin";

        /// <summary>默认 1：浮动圆钮，克制、冷色、以强调蓝收边。</summary>
        public static readonly WheelSkin Default = new(
            DefaultId, "默认 1", "Default 1",
            ShowSpokes: false,
            ShowCenterTicks: false,
            ShowInnerRing: true,
            RimAlpha: 0.85,
            InnerRingAlpha: 0.10,
            SpokeAlpha: 0,
            SlotGradient: 0.32,
            SlotRimAlpha: 0.28,
            PlateVignette: 0.18,
            GlowStrength: 1.0);

        /// <summary>默认 2：环形扇区，暖调底盘、米色双环、辐条与刻度，金铜色高亮。</summary>
        public static readonly WheelSkin Ring = new(
            RingId, "默认 2", "Default 2",
            ShowSpokes: true,
            ShowCenterTicks: true,
            ShowInnerRing: true,
            RimAlpha: 0.62,
            InnerRingAlpha: 0.28,
            SpokeAlpha: 0.14,
            SlotGradient: 0.58,
            SlotRimAlpha: 0.36,
            PlateVignette: 0.42,
            GlowStrength: 0.85,
            RingLayout: true);

        public static IReadOnlyList<WheelSkin> All { get; } = new[] { Default, Ring };

        /// <summary>这个 id 是否指向环形皮肤（含旧配置里的 genshin）。</summary>
        public static bool IsRing(string? id)
            => string.Equals(id, RingId, System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(id, RingLegacyId, System.StringComparison.OrdinalIgnoreCase);

        public static WheelSkin Get(string? id)
        {
            if (IsRing(id)) return Ring;

            foreach (var skin in All)
            {
                if (string.Equals(skin.Id, id, System.StringComparison.OrdinalIgnoreCase)) return skin;
            }

            return Default;
        }

        /// <summary>
        /// 皮肤的预设配色：背景 / 边框 / 选项 / 选中 / 文字。
        /// 设置面板在用户切换皮肤时直接把这一组装进外观设置。
        /// </summary>
        public static (string Background, string Border, string Item, string ItemSelected, string Text)
            PresetFor(string? id)
        {
            if (IsRing(id))
            {
                // 底盘接近不透明：半透明底盘压在亮色桌面/窗口上会被"洗白"，
                // 层次（双环、辐条、刻度）全都会看不见。
                return ("#E81B1922", "#F2E3C4", "#F72E2A36", "#FFE8B764", "#FFF7F0E3");
            }

            // 默认 1：保持与旧版一致的配色
            return ("#E61A1A2E", "#FF4A9EFF", "#E62A2A3A", "#FFFF6B35", "#FFFFFFFF");
        }

        /// <summary>把颜色按比例压暗（保留 alpha）。</summary>
        public static Color Darken(Color c, double amount)
        {
            double f = 1.0 - System.Math.Clamp(amount, 0, 1);
            return Color.FromArgb(c.A,
                (byte)System.Math.Round(c.R * f),
                (byte)System.Math.Round(c.G * f),
                (byte)System.Math.Round(c.B * f));
        }

        /// <summary>把颜色按比例提亮（保留 alpha）。</summary>
        public static Color Lighten(Color c, double amount)
        {
            double a = System.Math.Clamp(amount, 0, 1);
            return Color.FromArgb(c.A,
                (byte)System.Math.Round(c.R + (255 - c.R) * a),
                (byte)System.Math.Round(c.G + (255 - c.G) * a),
                (byte)System.Math.Round(c.B + (255 - c.B) * a));
        }

        /// <summary>替换 alpha（0~1 的比例，乘在原有 alpha 上）。</summary>
        public static Color Fade(Color c, double factor)
        {
            double a = System.Math.Clamp(c.A * factor, 0, 255);
            return Color.FromArgb((byte)System.Math.Round(a), c.R, c.G, c.B);
        }

        /// <summary>指定绝对 alpha（0~255）。</summary>
        public static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);
    }
}
