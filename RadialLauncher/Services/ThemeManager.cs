using System;
using System.Windows;
using System.Windows.Media;

namespace RadialLauncher.Services
{
    /// <summary>
    /// 界面主题（字体 / 深浅色）。
    ///
    /// <para>字体与画刷都用 DynamicResource 引用，所以运行中切换无需重启：
    /// 换字体只是替换一个 FontFamily 资源，换深浅色则是整份替换调色板字典
    /// （Palette.Light.xaml ⇄ Palette.Dark.xaml），引用会自动重新解析。</para>
    ///
    /// <para>为什么不直接改画刷的 Color：一旦画刷被 Application 级样式通过
    /// StaticResource 引用过，WPF 就会把它冻结（IsFrozen=true），再改就会抛
    /// “对象处于只读状态”。这一点是实测确认过的，别改回那种写法。</para>
    /// </summary>
    public static class ThemeManager
    {
        /// <summary>App.xaml 里声明的全局字体键名。</summary>
        public const string FontResourceKey = "AppFontFamily";

        private const string LightPalette = "Styles/Palette.Light.xaml";
        private const string DarkPalette = "Styles/Palette.Dark.xaml";

        /// <summary>当前是否深色主题。</summary>
        public static bool IsDark { get; private set; }

        /// <summary>应用界面字体；familyName 可为空（走默认回退链）。</summary>
        public static void ApplyUiFont(string? familyName)
        {
            var app = System.Windows.Application.Current;
            if (app == null) return;

            try
            {
                var family = FontCatalog.Resolve(familyName);
                app.Resources[FontResourceKey] = family;

                Logger.Info($"界面字体：{FontCatalog.BuildFamilyChain(familyName)}" +
                            $"（实际生效 {FontCatalog.DescribeEffective(familyName)}）");
            }
            catch (Exception ex)
            {
                Logger.Warn("设置界面字体失败：" + ex.Message);
            }
        }

        /// <summary>切换深色 / 浅色主题。整份替换调色板，动态资源随即刷新。</summary>
        public static void ApplyTheme(bool dark)
        {
            var app = System.Windows.Application.Current;
            if (app == null) return;

            try
            {
                var merged = app.Resources.MergedDictionaries;
                var wanted = dark ? DarkPalette : LightPalette;

                ResourceDictionary? existing = null;
                foreach (var dictionary in merged)
                {
                    if (IsPalette(dictionary)) { existing = dictionary; break; }
                }

                // 已经是目标主题就不动，免得白白重建一次资源
                if (existing != null && SourceOf(existing).EndsWith(
                        dark ? "Palette.Dark.xaml" : "Palette.Light.xaml", StringComparison.OrdinalIgnoreCase))
                {
                    IsDark = dark;
                    return;
                }

                var replacement = new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/" + wanted, UriKind.Absolute),
                };

                if (existing != null) merged[merged.IndexOf(existing)] = replacement;
                else merged.Insert(0, replacement);

                IsDark = dark;
                Logger.Info($"界面主题：{(dark ? "深色" : "浅色")}");
            }
            catch (Exception ex)
            {
                Logger.Warn("切换界面主题失败：" + ex.Message);
            }
        }

        private static bool IsPalette(ResourceDictionary dictionary)
        {
            var source = SourceOf(dictionary);
            return source.EndsWith("Palette.Light.xaml", StringComparison.OrdinalIgnoreCase)
                || source.EndsWith("Palette.Dark.xaml", StringComparison.OrdinalIgnoreCase);
        }

        private static string SourceOf(ResourceDictionary dictionary)
            => dictionary.Source?.OriginalString ?? "";

        /// <summary>给任意元素套上当前界面字体（代码创建的控件用）。</summary>
        public static void ApplyFontTo(FrameworkElement element)
        {
            var app = System.Windows.Application.Current;
            if (app == null || element == null) return;

            if (app.Resources[FontResourceKey] is FontFamily family)
                element.SetValue(System.Windows.Controls.Control.FontFamilyProperty, family);
        }

        /// <summary>
        /// 设置面板的控件不透明度：调低就能透出自定义背景。
        /// 只作用于设置面板自己的根容器，不影响托盘、轮盘与其它窗口。
        /// </summary>
        public static void ApplyPanelOpacity(FrameworkElement? root, double opacity)
        {
            if (root == null) return;

            var value = double.IsNaN(opacity) ? 1.0 : Math.Clamp(opacity, 0.25, 1.0);
            root.Opacity = value;
        }
    }
}
