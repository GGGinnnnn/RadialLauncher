using System;
using System.Diagnostics;
using System.Threading;
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
            var watch = Stopwatch.StartNew();

            // 托盘图标所在的视觉树已经加载完成，可以安心隐藏窗口了
            Hide();
            Logger.Info("主窗口已隐藏");

            // ---- 顺序很关键：先起服务（钩子 + 前台检测），再管托盘图标 ----
            //
            // 开机登录那一刻资源管理器往往还没把通知区准备好，创建托盘图标有可能失败，
            // 甚至卡在等 shell 回应上。旧顺序是「先托盘、后服务」，一旦卡在托盘那一步，
            // 后面的钩子就永远不会安装 —— 表现恰好就是用户报的那一串症状：
            // 没有托盘图标、按键全部没反应、只能去任务管理器结束进程再重开。
            // 现在两步各自兜底，任何一步出问题都不会拖死另一步。
            bool servicesOk = true;
            try
            {
                InitializeServices();
            }
            catch (Exception ex)
            {
                servicesOk = false;
                Logger.Error("启动服务失败", ex);
            }

            var servicesMs = watch.ElapsedMilliseconds;

            try
            {
                InitializeTrayIcon();
            }
            catch (Exception ex)
            {
                Logger.Error("初始化托盘图标失败", ex);
            }

            Logger.Info($"启动耗时：服务 {servicesMs} ms，托盘 {watch.ElapsedMilliseconds - servicesMs} ms");

            // 托盘看门狗：这次没成，或者成了但当时 shell 还没准备好，都由它兜住
            StartTrayWatchdog(servicesOk);

            // UI 线程看门狗：万一界面线程卡死，主动退出释放单实例锁
            StartUiWatchdog();

            if (_configService.Config.System.ShowTrayTip)
            {
                ToastWindow.Show(
                    Loc.T("Tray_Tooltip"),
                    Loc.Pick("按住触发键即可在鼠标处唤出轮盘。", "Hold a trigger key to open the wheel at the cursor."));
            }
        }

        // ==================== 托盘看门狗 ====================

        private DispatcherTimer? _trayWatchdog;
        private int _trayAttempts;

        /// <summary>创建失败时多久重试一次。</summary>
        private static readonly TimeSpan TrayRetryInterval = TimeSpan.FromMilliseconds(1500);

        /// <summary>最多重试几次（约 22 秒），之后放弃但不再影响其它功能。</summary>
        private const int TrayRetryMaxAttempts = 15;

        /// <summary>
        /// 启动后再盯一会儿托盘图标。
        ///
        /// <para>两件事：① 创建失败就重试，登录瞬间 shell 没就绪是很常见的失败原因；
        /// ② 创建成功也主动重挂一次 —— 有些情况下 Shell_NotifyIcon 会“成功但图标不出现”
        /// （通知区尚未初始化完），重挂一次就正常了。</para>
        ///
        /// <para>重挂用的是「置空再赋值」：IconSource 是依赖属性，赋同一个值不会触发变更，
        /// 必须让它先变一次。H.NotifyIcon 内部自己处理 TaskbarCreated（资源管理器重启后
        /// 自动重新添加图标），所以这里不需要额外监听 shell 广播。</para>
        /// </summary>
        private void StartTrayWatchdog(bool servicesOk)
        {
            if (_trayWatchdog != null) return;

            int refreshesLeft = HasTrayIcon() ? 2 : 0;

            _trayWatchdog = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TrayRetryInterval,
            };

            _trayWatchdog.Tick += (_, _) =>
            {
                _trayAttempts++;

                if (!HasTrayIcon())
                {
                    if (_trayAttempts > TrayRetryMaxAttempts)
                    {
                        Logger.Warn($"托盘图标重试 {_trayAttempts - 1} 次仍未成功，放弃。" +
                                    $"核心功能{(servicesOk ? "已" : "未")}启动，界面仍可用托盘菜单以外的方式打开。");
                        StopTrayWatchdog();
                        return;
                    }

                    Logger.Info($"托盘图标第 {_trayAttempts} 次重试");
                    try { InitializeTrayIcon(); }
                    catch (Exception ex) { Logger.Error("重试托盘图标失败", ex); }

                    return;
                }

                // 已经挂上了，再做一两次主动重挂，覆盖“成功但没显示”的情况
                if (refreshesLeft > 0)
                {
                    refreshesLeft--;
                    RefreshTrayIcon();
                }
                else
                {
                    StopTrayWatchdog();
                }
            };

            _trayWatchdog.Start();
        }

        /// <summary>
        /// 托盘图标是否已经挂上。
        /// 两条路径都要算：内存生成的 Icon（首选）和写文件再读回来的 IconSource（兜底）。
        /// 只判 IconSource 会让看门狗以为一直没成功，于是无限重试。
        /// </summary>
        private bool HasTrayIcon() => Tray.Icon != null || Tray.IconSource != null;

        /// <summary>把已挂上的托盘图标重挂一次（置空再赋值，强制走一遍 Shell_NotifyIcon）。</summary>
        private void RefreshTrayIcon()
        {
            try
            {
                var icon = Tray.IconSource;
                if (icon == null) return;

                Tray.IconSource = null;
                Tray.IconSource = icon;

                Logger.Debug("托盘图标已重挂一次");
            }
            catch (Exception ex)
            {
                Logger.Warn("重挂托盘图标失败：" + ex.Message);
            }
        }

        private void StopTrayWatchdog()
        {
            _trayWatchdog?.Stop();
            _trayWatchdog = null;
        }

        // ==================== UI 线程看门狗 ====================

        private Thread? _uiWatchdog;
        private volatile bool _uiWatchdogStop;
        private long _uiHeartbeat;
        private int _uiStuckCount;

        /// <summary>心跳间隔。</summary>
        private const int UiWatchdogIntervalMs = 5000;

        /// <summary>连续多少次收不到心跳就判定 UI 线程卡死（5s × 6 ≈ 30s）。</summary>
        private const int UiWatchdogMaxStuck = 6;

        /// <summary>
        /// UI 线程看门狗。
        ///
        /// <para>用户报过一种很难受的状态：程序“假活”——进程还在、托盘图标没了、功能全废，
        /// 而它还占着单实例互斥体，于是重新双击 exe 也进不来，只能去任务管理器结束进程。
        /// 那是 UI 线程卡死、而后台线程（激活监听）还活着造成的。</para>
        ///
        /// <para>这里给 UI 线程加个心跳：后台线程定时往 Dispatcher 投一次 ping。
        /// 连续 6 次（约 30 秒）投不进去，说明 UI 线程已经彻底不干活了，
        /// 那就记一条 ERROR 并主动退出 —— 进程一退，互斥体立刻释放，
        /// 用户下次双击就能正常启动，不必再开任务管理器。</para>
        ///
        /// <para>30 秒这个阈值是安全的：模态对话框、菜单弹层都会继续泵消息，
        /// 心跳照样能进；真能在 UI 线程上卡住 30 秒的，本来就是必须处理的故障。</para>
        /// </summary>
        private void StartUiWatchdog()
        {
            if (_uiWatchdog != null) return;

            _uiWatchdog = new Thread(() =>
            {
                while (!_uiWatchdogStop)
                {
                    long before = Interlocked.Read(ref _uiHeartbeat);

                    try
                    {
                        Dispatcher.BeginInvoke(new Action(() => Interlocked.Increment(ref _uiHeartbeat)),
                            DispatcherPriority.Normal);
                    }
                    catch
                    {
                        // Dispatcher 已经关了，正常退出
                        return;
                    }

                    Thread.Sleep(UiWatchdogIntervalMs);
                    if (_uiWatchdogStop) return;

                    if (Interlocked.Read(ref _uiHeartbeat) != before)
                    {
                        _uiStuckCount = 0;
                        continue;
                    }

                    _uiStuckCount++;
                    Logger.Warn($"界面线程已连续 {_uiStuckCount} 次（约 {_uiStuckCount * UiWatchdogIntervalMs / 1000} 秒）无响应");

                    if (_uiStuckCount < UiWatchdogMaxStuck) continue;

                    Logger.Error(
                        $"界面线程连续 {UiWatchdogMaxStuck} 次心跳无响应，判定为卡死。" +
                        "主动退出以释放单实例锁，避免变成必须手动结束的僵尸进程。");

                    try { Logger.EndSessionLog(); } catch { }
                    try { Logger.Flush(2000); } catch { }

                    // UI 线程已经不可用，走不了正常关闭流程，只能硬退
                    Environment.Exit(0);
                    return;
                }
            })
            {
                IsBackground = true,
                Name = "RadialLauncher.UiWatchdog",
            };

            _uiWatchdog.Start();
            Logger.Debug("界面线程看门狗已启动");
        }

        private void StopUiWatchdog() => _uiWatchdogStop = true;

        /// <summary>
        /// 托盘图标必须在视觉树加载之后、且用位图格式设置，
        /// 否则 H.NotifyIcon 会静默失败（DrawingImage 不受支持）。
        /// </summary>
        private void InitializeTrayIcon()
        {
            try
            {
                // 优先用"纯内存"的 Icon：不依赖数据目录可写。
                // 旧做法要先把 ICO 落到数据目录再读回来，数据目录一不可写就没有托盘图标。
                var icon = TrayIconFactory.CreateIcon();
                if (icon != null)
                {
                    Tray.Icon = icon;
                    Logger.Info("托盘图标已设置（内存生成）");
                    return;
                }

                // 兜底：退回老的"写文件再读回来"路径
                var frame = TrayIconFactory.GetIcon();
                if (frame == null)
                {
                    Logger.Warn("托盘图标生成失败，托盘将不可见");
                    return;
                }

                Tray.IconSource = frame;
                Logger.Info("托盘图标已设置（文件方式）");
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
                            $"Win={_configService.Config.Trigger.ByWin} Alt={_configService.Config.Trigger.ByAlt} " +
                            $"中键={_configService.Config.Trigger.ByMiddleMouse} " +
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
                StopTrayWatchdog();
                StopUiWatchdog();
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

            // 一次性构造完整快照交给钩子服务。它不再暴露逐个可写属性 ——
            // 那些属性会被 UI 线程直写、而钩子线程正在读，是典型的数据竞争。
            _hook.UpdateSettings(new InputHookService.HookSettings(
                ListenCtrl: config.Trigger.ByCtrl,
                ListenWin: config.Trigger.ByWin,
                ListenAlt: config.Trigger.ByAlt,
                ListenMiddle: config.Trigger.ByMiddleMouse,
                // 名字 → 虚拟键码只在这里解析一次：钩子回调里每秒要跑上百次，
                // 绝不能再去查表解析字符串。
                CustomTriggerVks: CustomTriggerKeys.ToVks(config.Trigger.CustomTriggerKeys),
                SuppressWin: config.Trigger.SuppressWinKey,
                SuppressMiddle: config.Trigger.SuppressMiddleClick,
                CancelOnOtherKey: config.Trigger.CancelOnOtherKey,

                // “松开执行”模式下左键=取消；关闭该选项后左键=选中执行
                CancelOnLeftClick: config.Appearance.ExecuteOnRelease,
                HoldThresholdMs: config.Trigger.HoldThresholdMs));
        }

        public void ApplyConfig()
        {
            var config = _configService.Config;

            Loc.Instance.SetLanguage(config.System.Language);
            ThemeManager.ApplyUiFont(config.System.UiFont);
            ThemeManager.ApplyTheme(config.System.DarkMode);
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

            // 配置里总开关关掉就不触发。
            // 注：设置面板打开期间由 _hook.Pause() 整段挂起钩子（见 OpenSettingsWindow），
            // 所以这里不需要再判断“面板是否打开”，也不必为录制按键留专门的分支。
            if (!config.Trigger.Enabled) return false;

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
                // 超时且修饰键仍按着：直接带着残留修饰键执行会把 Win+V 合成成
                // Ctrl+Win+V（轻则没反应、重则触发系统提示音）。这里主动把仍按着的
                // 修饰键全部补发一次“松开”，让这次执行的结果确定；补发后仍无法抬起
                // 时才放弃执行，避免把污染过的快捷键投给系统。
                var released = KeyStateHelper.ForceReleaseAllDown();

                if (KeyStateHelper.AnyModifierDown())
                {
                    Logger.Warn($"等待修饰键抬起超时（{KeyStateHelper.DescribeDown()} 仍按下），已放弃执行「{item.Name}」");
                    ToastWindow.Show(
                        string.Format(Loc.T("Msg_ActionBlockedModifier"), item.Name),
                        Loc.T("Msg_ActionBlockedModifierHint"),
                        error: true);
                    return;
                }

                Logger.Warn($"等待修饰键抬起超时，已补发松开（{string.Join("+", released)}）后执行「{item.Name}」");
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

            var settings = new SettingsWindow(this);
            _settings = settings;
            settings.Closed += (_, _) =>
            {
                _hook.Resume();

                // 保存/取消路径已在关闭前应用过配置，这里只兜底“直接点 X 关闭”的情况
                if (!settings.ConfigAppliedOnClose) ApplyConfig();
            };
            settings.Show();
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
                Logger.Flush();   // 日志现在是异步落盘，先确保文件已经写出来
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
