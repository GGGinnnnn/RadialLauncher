using System;
using System.Globalization;
using System.Windows.Media;

namespace RadialLauncher.Services
{
    /// <summary>颜色与画刷的解析 / 格式化工具。</summary>
    public static class BrushHelper
    {
        /// <summary>把 #RGB / #ARGB / #RRGGBB / #AARRGGBB 解析成 Color。</summary>
        public static bool TryParseColor(string? text, out Color color)
        {
            color = Colors.Transparent;
            if (string.IsNullOrWhiteSpace(text)) return false;

            var value = text.Trim();
            if (value.StartsWith('#')) value = value[1..];

            // #RGB
            if (value.Length == 3)
            {
                value = new string(new[]
                {
                    value[0], value[0], value[1], value[1], value[2], value[2],
                });
            }

            if (value.Length != 6 && value.Length != 8) return false;

            if (!uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint raw))
                return false;

            if (value.Length == 6)
            {
                color = Color.FromRgb((byte)(raw >> 16), (byte)(raw >> 8), (byte)raw);
            }
            else
            {
                color = Color.FromArgb((byte)(raw >> 24), (byte)(raw >> 16), (byte)(raw >> 8), (byte)raw);
            }

            return true;
        }

        public static Color ParseColor(string? text, Color fallback)
            => TryParseColor(text, out var color) ? color : fallback;

        /// <summary>输出 #AARRGGBB。</summary>
        public static string ToHex(Color color)
            => $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

        /// <summary>输出 #RRGGBB（丢掉 alpha）。</summary>
        public static string ToHexNoAlpha(Color color)
            => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

        public static SolidColorBrush FromHex(string? hex, Color fallback)
        {
            var brush = new SolidColorBrush(ParseColor(hex, fallback));
            brush.Freeze();
            return brush;
        }

        public static SolidColorBrush FromColor(Color color, double opacity = 1.0)
        {
            var brush = new SolidColorBrush(color) { Opacity = opacity };
            brush.Freeze();
            return brush;
        }

        /// <summary>取颜色相对亮度，用于决定叠在上面用黑字还是白字。</summary>
        public static bool IsLight(Color color)
            => (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) > 150;
    }
}
