using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RadialLauncher.Services;

namespace RadialLauncher.Views
{
    /// <summary>挑一个 emoji 当图标，或选择自动从目标程序提取图标。</summary>
    public partial class IconPickerWindow : Window
    {
        /// <summary>选中的 emoji（当 IconPath 为 auto 时忽略）。</summary>
        public string SelectedIcon { get; private set; } = "●";

        /// <summary>"auto" 表示自动提取，空表示不使用图片。</summary>
        public string SelectedIconPath { get; private set; } = "";

        private static readonly string[] Emojis =
        {
            // 常用应用
            "💬", "💚", "⚔", "🎮", "🎵", "🎬", "📺", "📷", "📞", "✉",
            "🌐", "🧭", "📧", "🗓", "📝", "📊", "📈", "🧮", "💳", "🛒",
            // 文件与目录
            "📁", "📂", "🗂", "🗃", "📄", "📃", "📋", "📌", "📎", "🔖",
            "💾", "💿", "🗄", "🗑", "📥", "📤", "📦", "🗜", "🔍", "🔎",
            // 系统
            "⚙", "🔧", "🔨", "🛠", "🖥", "💻", "⌨", "🖱", "🖨", "📱",
            "🔒", "🔓", "🔑", "🗝", "🛡", "⚠", "⏻", "🌙", "🛌", "🚪",
            "🔄", "♻", "⏯", "⏭", "⏮", "⏹", "🔊", "🔉", "🔇", "🎚",
            // 编辑与开发
            "✂", "✏", "🖊", "🖌", "🎨", "🧹", "🧲", "🐞", "🧪", "🧬",
            "🌿", "🔀", "🔁", "▶", "⏸", "⏺", "⏏", "🧑‍💻", "🤖", "🧩",
            // 符号与标记
            "⭐", "🌟", "✨", "❤", "🧡", "💛", "💙", "💜", "✅", "❌",
            "➕", "➖", "⬅", "➡", "⬆", "⬇", "↩", "↪", "🏠", "🏁",
            "🔔", "🔕", "📢", "📍", "🎯", "🚀", "💡", "🔥", "❄", "⚡",
            "🍀", "🌈", "☕", "🍕", "🎁", "🧭", "🗺", "🌍", "🕐", "⏰",
        };

        public IconPickerWindow(string currentIcon, string currentIconPath)
        {
            InitializeComponent();

            SelectedIcon = string.IsNullOrWhiteSpace(currentIcon) ? "●" : currentIcon;
            SelectedIconPath = currentIconPath ?? "";
            CustomBox.Text = SelectedIcon;

            BuildEmojiGrid();
        }

        /// <summary>弹出图标选择器；取消返回 null。</summary>
        public static (string Icon, string IconPath)? Pick(Window? owner, string currentIcon, string currentIconPath)
        {
            var window = new IconPickerWindow(currentIcon, currentIconPath);
            if (owner != null && owner.IsVisible) window.Owner = owner;

            if (window.ShowDialog() != true) return null;
            return (window.SelectedIcon, window.SelectedIconPath);
        }

        private void BuildEmojiGrid()
        {
            foreach (var emoji in Emojis)
            {
                var button = new Button
                {
                    Content = emoji,
                    Width = 44,
                    Height = 44,
                    Margin = new Thickness(0, 0, 5, 5),
                    FontSize = 20,
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Background = Brushes.Transparent,
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x22, 0, 0, 0)),
                    BorderThickness = new Thickness(1),
                    ToolTip = emoji,
                };

                var captured = emoji;
                button.Click += (_, _) =>
                {
                    SelectedIcon = captured;
                    SelectedIconPath = "";
                    DialogResult = true;
                    Close();
                };

                EmojiList.Items.Add(button);
            }
        }

        private void OnUseCustom(object sender, RoutedEventArgs e)
        {
            var text = CustomBox.Text?.Trim() ?? "";
            if (text.Length == 0) return;

            SelectedIcon = text;
            SelectedIconPath = "";
            DialogResult = true;
            Close();
        }

        private void OnUseAuto(object sender, RoutedEventArgs e)
        {
            SelectedIconPath = "auto";
            DialogResult = true;
            Close();
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
