using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using RadialLauncher.Models;
using RadialLauncher.Services;
using RadialLauncher.Views;

namespace RadialLauncher
{
    /// <summary>
    /// 隐藏的托盘宿主窗口，同时是整个程序的调度中心：
    /// 全局钩子 → 环境判定 → 轮盘 → 执行动作。
    /// </summary>
    public partial class MainWindow : Window, IAppHost
    {
        private readonly ConfigService _configService;
        private readonly InputHookService _hook = new();
        private readonly WindowDetectorService _detector = new();
        private readonly ActionExecutor _executor = new();

        private WheelWindow? _wheel;
        private SettingsWindow? _settings;

        private bool _wheelShown;
        private bool _executedByClick;
        private DispatcherTimer? _testTimer;

        // ===== 执行前等待修饰键抬起 =====
        private WheelItem? _pendingItem;
        private DispatcherTimer? _releaseTimer;
        private long _releaseDeadline;

        /// <summary>最多等修饰键抬起多久；超过就照常执行，避免动作被无限期拖住。</summary>
        private const int ModifierReleaseTimeoutMs = 2000;

        public MainWindow(ConfigService configService)
        {
            _configService = configService;

            InitializeComponent();

            Loaded += OnLoaded;
            SourceInitialized += OnSourceInitialized;
        }

        // ==================== IAppHost ====================

        public ConfigService ConfigService => _configService;

        public WindowDetectorService Detector => _detector;

        public bool IsRunning => _hook.IsRunning;

        // ==================== 生命周期 ====================

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            // 不出现在 Alt+Tab 里
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;

                int exStyle = NativeMethods.GetWindowLongW(hwnd, NativeMethods.GWL_EXSTYLE);
                exStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
                NativeMethods.SetWindowLongW(hwnd, NativeMethods.GWL_EXSTYLE, exStyle);
            }
            catch (Exception ex)
            {
                Logger.Debug("设置主窗口扩展样式失败：" + ex.Message);
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // 托盘图标所在的视觉树已经加载完成，可以安心隐藏窗口了
            Hide();
            Logger.Info("主窗口已隐藏，托盘图标就绪");

            InitializeTrayIcon();
            InitializeServices();

            if (_configService.Config.System.ShowTrayTip)
            {
                ToastWindow.Show(
                    Loc.T("Tray_Tooltip"),
                    Loc.Pick("按住触发键即可在鼠标处唤出轮盘。", "Hold a trigger key to open the wheel at the cursor."));
            }
        }

        /// <summary>
        /// 托盘图标必须在视觉树加载之后、且用位图格式设置，
        /// 否则 H.NotifyIcon 会静默失败（DrawingImage 不受支持）。
        /// </summary>
        private void InitializeTrayIcon()
        {
            try
            {
                var icon = TrayIconFactory.GetIcon();
                if (icon == null)
                {
                    Logger.Warn("托盘图标生成失败，托盘将不可见");
                    return;
                }

                Tray.IconSource = icon;
                Logger.Info("托盘图标已设置");
            }
            catch (Exception ex)
            {
                Logger.Error("设置托盘图标失败", ex);
            }
        }

        private void InitializeServices()
        {
            try
            {
                _hook.CanTrigger = CanTrigger;

                _hook.OnTrigger += OnTrigger;
                _hook.OnRelease += OnRelease;
                _hook.OnCancel += OnCancel;

                SyncHookSettings();
                _detector.Start();

                if (_configService.Config.Trigger.Enabled) _hook.Start();

                Logger.Info($"服务已启动：触发键 Ctrl={_configService.Config.Trigger.ByCtrl} " +
                            $"Win={_configService.Config.Trigger.ByWin} 中键={_configService.Config.Trigger.ByMiddleMouse} " +
                            $"延时={_configService.Config.Trigger.HoldThresholdMs}ms");
            }
            catch (Exception ex)
            {
                Logger.Error("初始化失败", ex);
                ToastWindow.Show("Radial Launcher", "初始化失败：" + ex.Message, error: true, seconds: 6);
            }
        }

        /// <summary>程序退出时的清理。</summary>
        public void Shutdown()
        {
            try
            {
                _testTimer?.Stop();
                _releaseTimer?.Stop();
                _pendingItem = null;

                // 退出前记下钩子回调的最长耗时，便于判断有没有出现阻塞
                if (_hook.MaxHookMs > 0)
                    Logger.Info($"钩子回调最长耗时 {_hook.MaxHookMs:0.00} ms");

                _hook.Dispose();
                _detector.Stop();
                _wheel?.Close();
                _settings?.Close();
            }
            catch (Exception ex)
            {
                Logger.Error("清理资源失败", ex);
            }
        }

        // ==================== 配置 ====================

        private void SyncHookSettings()
        {
            var config = _configService.Config;

            _hook.ListenCtrl = config.Trigger.ByCtrl;
            _hook.ListenWin = config.Trigger.ByWin;
            _hook.ListenMiddle = config.Trigger.ByMiddleMouse;
            _hook.SuppressWin = config.Trigger.SuppressWinKey;
            _hook.SuppressMiddle = config.Trigger.SuppressMiddleClick;
            _hook.CancelOnOtherKey = config.Trigger.CancelOnOtherKey;
            _hook.HoldThresholdMs = config.Trigger.HoldThresholdMs;

            // “松开执行”模式下左键=取消；关闭该选项后左键=选中执行
            _hook.CancelOnLeftClick = config.Appearance.ExecuteOnRelease;
        }

        public void ApplyConfig()
        {
            var config = _configService.Config;

            Loc.Instance.SetLanguage(config.System.Language);
            ThemeManager.ApplyUiFont(config.System.UiFont);
            SyncHookSettings();

            if (config.Trigger.Enabled)
            {
                if (!_hook.IsRunning) _hook.Start();
            }
            else if (_hook.IsRunning)
            {
                _hook.Stop();
            }

            Logger.Info("配置已应用");
        }

        public void PauseHook() => _hook.Pause();

        public void ResumeHook() => _hook.Resume();

        public void OpenDataFolder()
        {
            try
            {
                System.IO.Directory.CreateDirectory(ConfigService.DataDirectory);
                Process.Start(new ProcessStartInfo("explorer.exe")
                {
                    Arguments = $"\"{ConfigService.DataDirectory}\"",
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                Logger.Error("打开配置目录失败", ex);
            }
        }

        // ==================== 触发判定 ====================

        /// <summary>钩子在阈值到达时调用：决定这次按住要不要唤出轮盘。</summary>
        private bool CanTrigger()
        {
            var config = _configService.Config;

            if (!config.Trigger.Enabled) return false;
            if (_settings is { IsVisible: true, IsRecordingKey: true }) return false;

            return _detector.IsAllowed(config);
        }

        // ==================== 轮盘 ====================

        private void OnTrigger()
        {
            ShowWheelWith(_configService.Config);
        }

        private void ShowWheelWith(AppConfig config)
        {
            try
            {
                var info = _detector.GetCurrent(force: true);
                var wheel = ProfileService.Resolve(config, info);

                _wheel ??= CreateWheelWindow();

                _executedByClick = false;
                _wheelShown = true;

                _wheel.ShowWheel(config, wheel);

                Logger.Info($"轮盘已唤出（方向数 {wheel.ItemCount}，可用 {wheel.Items.Count} 项" +
                            (wheel.IsProfile ? $"，应用专属：{wheel.Title}" : "") + "）");
            }
            catch (Exception ex)
            {
                Logger.Error("唤出轮盘失败", ex);
                _wheelShown = false;
            }
        }

        private WheelWindow CreateWheelWindow()
        {
            var wheel = new WheelWindow();
            wheel.ItemActivated += OnWheelItemActivated;
            return wheel;
        }

        private void OnWheelItemActivated(object? sender, WheelItem item)
        {
            // 鼠标点击执行（仅当“松开执行”关闭时可用）
            _executedByClick = true;
            HideWheelInternal();
            RunItem(item);
        }

        private void OnRelease()
        {
            if (!_wheelShown)
            {
                Logger.Debug("松开时轮盘已隐藏，忽略");
                return;
            }

            var item = _wheel?.SelectedItem;
            HideWheelInternal();

            if (_executedByClick)
            {
                _executedByClick = false;
                return;
            }

            if (item == null)
            {
                Logger.Debug("松开时没有选中任何项");
                return;
            }

            if (!_configService.Config.Appearance.ExecuteOnRelease)
            {
                Logger.Debug("已关闭“松开执行”，忽略");
                return;
            }

            RunItem(item);
        }

        private void OnCancel()
        {
            HideWheelInternal();
            Logger.Info("轮盘已取消（未执行任何功能）");
        }

        private void HideWheelInternal()
        {
            _wheel?.HideWheel();
            _wheelShown = false;
        }

        private void RunItem(WheelItem item)
        {
            // 触发键本身就是修饰键（Ctrl / Win），所以“松开触发键”和“合成快捷键”
            // 之间存在一个真实存在的竞态：Windows 的按键状态表对物理按键的更新
            // 落后于低级钩子，此刻合成 Win+V 会被系统算成 Ctrl+Win+V
            // —— 轻则没反应，重则触发系统提示音。
            // 因此执行前先确认修饰键真的抬起来了，没抬起就等它抬起再执行。
            if (KeyStateHelper.AnyModifierDown())
            {
                _pendingItem = item;
                StartModifierReleaseWatch();
                Logger.Info($"执行前 {KeyStateHelper.DescribeDown()} 仍按下，等它抬起后再执行「{item.Name}」");
                return;
            }

            ExecuteItem(item);
        }

        private void StartModifierReleaseWatch()
        {
            _releaseTimer ??= new DispatcherTimer(DispatcherPriority.Normal)
            {
                Interval = TimeSpan.FromMilliseconds(10),
            };

            _releaseDeadline = Environment.TickCount64 + ModifierReleaseTimeoutMs;
            _releaseTimer.Tick -= OnModifierReleaseTick;
            _releaseTimer.Tick += OnModifierReleaseTick;
            _releaseTimer.Start();
        }

        private void OnModifierReleaseTick(object? sender, EventArgs e)
        {
            bool stillDown = KeyStateHelper.AnyModifierDown();
            bool timedOut = Environment.TickCount64 >= _releaseDeadline;

            if (stillDown && !timedOut) return;

            _releaseTimer?.Stop();

            var item = _pendingItem;
            _pendingItem = null;
            if (item == null) return;

            if (stillDown)
            {
                Logger.Warn($"等待修饰键抬起超时（{KeyStateHelper.DescribeDown()} 仍按下），照常执行「{item.Name}」");
            }

            ExecuteItem(item);
        }

        private void ExecuteItem(WheelItem item)
        {
            var result = _executor.Execute(item);

            if (result.Success)
            {
                Logger.Info($"已执行「{item.Name}」（{item.ActionType}）");
                return;
            }

            Logger.Warn($"执行「{item.Name}」失败：{result.Message}");
            ToastWindow.Show(
                string.Format(Loc.T("Msg_ActionFailed"), item.Name),
                result.Message,
                error: true);
        }

        // ==================== 托盘菜单 ====================

        private void OnTrayDoubleClick(object sender, RoutedEventArgs e) => OpenSettingsWindow();

        private void OpenSettings(object sender, RoutedEventArgs e) => OpenSettingsWindow();

        /// <summary>打开设置面板（也用于 --settings 命令行参数）。</summary>
        public void OpenSettings() => OpenSettingsWindow();

        private void OpenSettingsWindow()
        {
            if (_settings is { IsVisible: true })
            {
                _settings.Activate();
                return;
            }

            // 设置面板打开期间暂停全局钩子，避免在文本框里敲字被拦截
            _hook.Pause();

            _settings = new SettingsWindow(this);
            _settings.Closed += (_, _) =>
            {
                _hook.Resume();
                ApplyConfig();
            };
            _settings.Show();
        }

        private void TestWheelClick(object sender, RoutedEventArgs e) => TestWheel(_configService.Config);

        /// <summary>用指定配置显示一次轮盘，5 秒后自动收起。</summary>
        public void TestWheel(AppConfig config)
        {
            if (_wheelShown) return;

            ShowWheelWith(config);

            _testTimer ??= new DispatcherTimer();
            _testTimer.Stop();
            _testTimer.Interval = TimeSpan.FromSeconds(5);
            _testTimer.Tick -= OnTestTimerTick;
            _testTimer.Tick += OnTestTimerTick;
            _testTimer.Start();
        }

        private void OnTestTimerTick(object? sender, EventArgs e)
        {
            _testTimer?.Stop();
            HideWheelInternal();
        }

        private void ReloadConfig(object sender, RoutedEventArgs e)
        {
            _configService.Load();
            ApplyConfig();

            ToastWindow.Show(
                Loc.T("Tray_Reload"),
                Loc.Pick("配置已重新加载。", "Configuration reloaded."));
        }

        private void OpenLog(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!System.IO.File.Exists(Logger.LogPath)) Logger.Info("日志窗口被打开");
                Process.Start(new ProcessStartInfo(Logger.LogPath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Logger.Error("打开日志失败", ex);
            }
        }

        private void ExitApp(object sender, RoutedEventArgs e)
        {
            Logger.Info("用户从托盘菜单退出");
            Shutdown();
            System.Windows.Application.Current.Shutdown();
        }
    }
}
