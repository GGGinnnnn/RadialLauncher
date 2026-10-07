using System;
using System.Windows;
using System.Windows.Media;

namespace RadialLauncher.Services
{
    /// <summary>
    /// 把用户选的界面字体应用到全局资源上。
    /// 用 DynamicResource 引用，所以运行中换字体无需重启。
    /// </summary>
    public static class ThemeManager
    {
        /// <summary>App.xaml 里声明的全局字体键名。</summary>
        public const string FontResourceKey = "AppFontFamily";

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

        /// <summary>给任意元素套上当前界面字体（代码创建的控件用）。</summary>
        public static void ApplyFontTo(FrameworkElement element)
        {
            var app = System.Windows.Application.Current;
            if (app == null || element == null) return;

            if (app.Resources[FontResourceKey] is FontFamily family)
                element.SetValue(System.Windows.Controls.Control.FontFamilyProperty, family);
        }
    }
}
