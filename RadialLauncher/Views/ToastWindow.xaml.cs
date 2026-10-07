using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace RadialLauncher.Views
{
    /// <summary>
    /// 屏幕右下角的轻量提示。同一时间只保留一个实例，重复调用会替换内容并重新计时。
    /// </summary>
    public partial class ToastWindow : Window
    {
        private static ToastWindow? _current;
        private static DispatcherTimer? _timer;

        private ToastWindow()
        {
            InitializeComponent();
            ShowActivated = false;
        }

        /// <summary>显示一条提示，几秒后自动淡出。</summary>
        public static void Show(string title, string message = "", bool error = false, double seconds = 3.0)
        {
            try
            {
                var app = System.Windows.Application.Current;
                if (app == null) return;

                app.Dispatcher.Invoke(() => ShowCore(title, message, error, seconds));
            }
            catch
            {
                // 提示失败不影响功能
            }
        }

        private static void ShowCore(string title, string message, bool error, double seconds)
        {
            _current ??= new ToastWindow();

            var toast = _current;
            toast.TitleText.Text = title;
            toast.BodyText.Text = message;
            toast.BodyText.Visibility = string.IsNullOrWhiteSpace(message)
                ? Visibility.Collapsed
                : Visibility.Visible;

            var accent = error
                ? Color.FromRgb(0xFF, 0x6B, 0x6B)
                : Color.FromRgb(0x4A, 0x9E, 0xFF);

            toast.IconText.Foreground = new SolidColorBrush(accent);
            toast.IconText.Text = error ? "⚠" : "●";
            toast.Card.BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, accent.R, accent.G, accent.B));

            // 定位到工作区右下角
            toast.UpdateLayout();
            var work = SystemParameters.WorkArea;
            toast.Left = work.Right - toast.ActualWidth - 8;
            toast.Top = work.Bottom - toast.ActualHeight - 8;

            if (!toast.IsVisible) toast.Show();
            toast.Topmost = true;   // 不调用 Activate，避免抢走焦点

            toast.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));

            _timer ??= new DispatcherTimer();
            _timer.Stop();
            _timer.Interval = TimeSpan.FromSeconds(Math.Max(0.8, seconds));
            _timer.Tick -= OnTick;
            _timer.Tick += OnTick;
            _timer.Start();
        }

        private static void OnTick(object? sender, EventArgs e)
        {
            _timer?.Stop();
            if (_current == null) return;

            var toast = _current;
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(220));
            fade.Completed += (_, _) => toast.Hide();
            toast.BeginAnimation(OpacityProperty, fade);
        }
    }
}
