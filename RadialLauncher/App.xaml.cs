using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using RadialLauncher.Services;

namespace RadialLauncher
{
    public partial class App : System.Windows.Application
    {
        private Mutex? _instanceMutex;
        private MainWindow? _main;

        private EventWaitHandle? _activateEvent;
        private EventWaitHandle? _ackEvent;
        private EventWaitHandle? _quitEvent;
        private ManualResetEvent? _stopListener;
        private Thread? _listenerThread;

        private const string MutexName = @"Local\RadialLauncher.SingleInstance";

        /// <summary>第二个实例用它来请求“把设置面板弹出来”。</summary>
        private const string ActivateEventName = @"Local\RadialLauncher.Activate";

        /// <summary>已有实例用它回执，证明自己还活着。</summary>
        private const string AckEventName = @"Local\RadialLauncher.Ack";

        /// <summary>请求已有实例优雅退出（RadialLauncher.exe --quit）。</summary>
        private const string QuitEventName = @"Local\RadialLauncher.Quit";

        /// <summary>等回执的最长时间；超时就认为对方是卡死的残留进程。</summary>
        private const int ActivationTimeoutMs = 2500;

        /// <summary>提权重启时带上的标记，用来区分“重复启动”和“接管旧进程”。</summary>
        public const string RelaunchArgument = "--relaunch";

        /// <summary>忽略单实例检测强行启动（残留进程占着互斥体时的逃生口）。</summary>
        public const string ForceArgument = "--force";

        /// <summary>让已在运行的实例优雅退出。</summary>
        public const string QuitArgument = "--quit";

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            bool isRelaunch = Array.Exists(e.Args,
                a => string.Equals(a, RelaunchArgument, StringComparison.OrdinalIgnoreCase));

            bool forceStart = Array.Exists(e.Args,
                a => string.Equals(a, ForceArgument, StringComparison.OrdinalIgnoreCase));

            // ---- RadialLauncher.exe --quit：让已运行的实例优雅退出 ----
            if (Array.Exists(e.Args, a => string.Equals(a, QuitArgument, StringComparison.OrdinalIgnoreCase)))
            {
                RequestRunningInstanceQuit();
                Shutdown();
                return;
            }

            // ---- 单实例 ----
            _instanceMutex = new Mutex(true, MutexName, out bool createdNew);

            if (!createdNew && !forceStart)
            {
                // 1) 提权重启：旧进程马上就要退出，短暂等待就能接手它的互斥体。
                bool acquired = isRelaunch && WaitForMutex(_instanceMutex, TimeSpan.FromSeconds(8));

                // 2) 已经有实例在跑：请它把设置面板弹出来，顺便当作存活探测。
                if (!acquired && TryActivateRunningInstance())
                {
                    Logger.Info("已有实例响应激活请求，本进程退出");
                    Shutdown();
                    return;
                }

                // 3) 没有任何实例应答 —— 极可能是卡死的残留进程（例如低级钩子死锁后
                //    连 TerminateProcess 都退不掉的僵尸进程）。这种情况下绝不能把用户
                //    挡在门外，直接按新实例继续启动。残留进程已经不占钩子，不会冲突。
                if (!acquired)
                {
                    Logger.Warn("已有实例未响应激活请求，可能是卡死的残留进程；本次按 --force 方式继续启动");
                }
            }
            else if (forceStart)
            {
                Logger.Info("收到 --force，跳过单实例检测");
            }

            // ---- 未处理异常兜底，避免整个进程静默退出 ----
            DispatcherUnhandledException += (_, args) =>
            {
                Logger.Error("界面线程未处理异常", args.Exception);
                args.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                Logger.Error("后台线程未处理异常", args.ExceptionObject as Exception);

            Logger.Info("================ Radial Launcher 启动 ================");
            Logger.Info($"版本 {typeof(App).Assembly.GetName().Version}，管理员={ElevationService.IsElevated}");

            // ---- 配置 ----
            var configService = new ConfigService();
            configService.Load();
            Loc.Instance.SetLanguage(configService.Config.System.Language);
            ThemeManager.ApplyUiFont(configService.Config.System.UiFont);

            // ---- 诊断模式：RadialLauncher.exe --selftest ----
            // 逐个加载全部窗口，验证 XAML、静态资源与快捷键目录在运行时都是好的。
            if (Array.Exists(e.Args, a => string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase)))
            {
                int exitCode = RunSelfTest(configService);
                Shutdown(exitCode);
                return;
            }

            // ---- 管理员提权 ----
            if (configService.Config.System.RunAsAdmin && !ElevationService.IsElevated)
            {
                if (ElevationService.IsUserInAdminGroup())
                {
                    Logger.Info("配置要求管理员权限，尝试提权重启");
                    if (ElevationService.RestartElevated())
                    {
                        Shutdown();
                        return;
                    }
                    Logger.Warn("提权被取消，继续以普通权限运行");
                }
                else
                {
                    Logger.Warn("当前账户不在管理员组，无法提权");
                }
            }

            // ---- 同步开机自启状态 ----
            if (configService.Config.System.AutoStart != AutoStartService.IsEnabled())
                AutoStartService.SetEnabled(configService.Config.System.AutoStart);

            // ---- 主窗口（隐藏的托盘宿主）----
            _main = new MainWindow(configService);
            MainWindow = _main;
            _main.Show();

            // ---- 让第二个实例能唤醒本实例（同时充当存活探测）----
            StartActivationListener();

            // ---- 可选：启动后直接打开设置面板（RadialLauncher.exe --settings）----
            if (Array.Exists(e.Args, a => string.Equals(a, "--settings", StringComparison.OrdinalIgnoreCase)))
            {
                Dispatcher.BeginInvoke(new Action(() => _main?.OpenSettings()),
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
        }

        /// <summary>
        /// 诊断模式：不安装任何钩子，逐个加载所有窗口并在关闭时检查异常。
        /// 结果同时写到控制台和 %AppData%\RadialLauncher\selftest.log。
        /// 返回进程退出码，0 表示全部通过。
        /// </summary>
        private static int RunSelfTest(ConfigService configService)
        {
            try { NativeMethods.AttachConsole(-1); } catch { /* 没有父控制台就算了 */ }

            var log = new System.Text.StringBuilder();

            void Write(string line)
            {
                log.AppendLine(line);
                try { Console.WriteLine(line); } catch { }
            }

            Write("Radial Launcher 自检");
            Write("====================");

            int failed = 0;

            // ---- 快捷键目录校验 ----
            int badKeys = 0;
            var categories = new System.Collections.Generic.HashSet<string>();
            foreach (var item in ShortcutCatalog.Items)
            {
                categories.Add(item.CategoryZh);
                if (!ShortcutCatalog.IsValidKeys(item.Keys, out var error))
                {
                    Write($"  [目录] 无法解析 {item.Id} ({item.Keys})：{error}");
                    badKeys++;
                }
            }

            Write($"  快捷键目录：{ShortcutCatalog.Items.Count} 条，{categories.Count} 个分类，{badKeys} 条解析失败");
            if (badKeys > 0) failed++;

            // ---- 内置功能校验 ----
            Write($"  内置功能：{BuiltinCatalog.Items.Count} 项");

            // ---- 本地化键校验 ----
            failed += CheckLocalizationKeys(Write);

            // ---- 窗口加载校验 ----
            MainWindow? host = null;
            var windows = new (string Name, Func<Window> Create)[]
            {
                ("MainWindow.xaml", () => host = new MainWindow(configService)),
                ("SettingsWindow.xaml", () => new Views.SettingsWindow(host!)),
                ("WheelWindow.xaml", () => new Views.WheelWindow()),
                ("ToastWindow.xaml", () => PrivateWindow(typeof(Views.ToastWindow))),
                ("ColorPickerWindow.xaml", () => new Views.ColorPickerWindow(System.Windows.Media.Colors.White)),
                ("ShortcutPickerWindow.xaml", () => new Views.ShortcutPickerWindow()),
                ("IconPickerWindow.xaml", () => new Views.IconPickerWindow("●", "")),
                ("ScreenColorPickerWindow.xaml", () => PrivateWindow(typeof(Views.ScreenColorPickerWindow))),
            };

            foreach (var (name, create) in windows)
            {
                try
                {
                    var window = create();
                    window.Close();
                    Write($"  [通过] {name}");
                }
                catch (Exception ex)
                {
                    Write($"  [失败] {name}");
                    Write(Describe(ex));
                    failed++;
                }
            }

            Write("====================");
            Write(failed == 0 ? "自检全部通过" : $"自检失败 {failed} 项");

            Logger.Info(failed == 0 ? "自检全部通过" : $"自检失败 {failed} 项");

            try
            {
                System.IO.Directory.CreateDirectory(ConfigService.DataDirectory);
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(ConfigService.DataDirectory, "selftest.log"),
                    log.ToString(), System.Text.Encoding.UTF8);
            }
            catch { }

            return failed;
        }

        /// <summary>
        /// 校验 XAML 绑定与代码里引用到的本地化键都真的定义了。
        /// 漏掉的键会在界面上直接显示成键名（占位符），肉眼很容易漏看，所以放进自检。
        /// </summary>
        private static int CheckLocalizationKeys(Action<string> write)
        {
            var projectDir = FindProjectDirectory();
            if (projectDir == null)
            {
                write("  [跳过] 找不到源码目录，不做本地化键校验");
                return 0;
            }

            var missing = new System.Collections.Generic.SortedDictionary<string, string>();
            int boundCount = 0;

            foreach (var file in EnumerateSourceFiles(projectDir, "*.xaml"))
            {
                foreach (Match match in Regex.Matches(File.ReadAllText(file), @"\{Binding\s+\[([A-Za-z0-9_]+)\]"))
                {
                    boundCount++;
                    Remember(missing, match.Groups[1].Value, file);
                }
            }

            foreach (var file in EnumerateSourceFiles(projectDir, "*.cs"))
            {
                foreach (Match match in Regex.Matches(File.ReadAllText(file), @"Loc\.T\(""([A-Za-z0-9_]+)""\)"))
                {
                    Remember(missing, match.Groups[1].Value, file);
                }
            }

            write($"  本地化键：XAML 绑定 {boundCount} 处，已定义 {Loc.Keys.Count} 个，缺失 {missing.Count} 个");

            foreach (var pair in missing)
                write($"        缺失 {pair.Key}（{pair.Value}）");

            return missing.Count == 0 ? 0 : 1;
        }

        private static void Remember(
            System.Collections.Generic.SortedDictionary<string, string> missing, string key, string file)
        {
            if (Loc.HasKey(key)) return;
            if (!missing.ContainsKey(key)) missing[key] = Path.GetFileName(file);
        }

        /// <summary>枚举源码文件，跳过 bin/obj 里的生成产物。</summary>
        private static System.Collections.Generic.IEnumerable<string> EnumerateSourceFiles(
            string root, string pattern)
        {
            foreach (var file in Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
            {
                var normalized = file.Replace('\\', '/');
                if (normalized.Contains("/bin/") || normalized.Contains("/obj/")) continue;
                yield return file;
            }
        }

        /// <summary>从输出目录往上找带 .csproj 的目录。</summary>
        private static string? FindProjectDirectory()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            for (int i = 0; i < 8 && directory != null; i++)
            {
                try
                {
                    if (directory.EnumerateFiles("*.csproj").Any()) return directory.FullName;
                }
                catch
                {
                    return null;
                }

                directory = directory.Parent;
            }

            return null;
        }

        /// <summary>把异常链和栈顶几帧拼成可读文本。</summary>
        private static string Describe(Exception ex)
        {
            var sb = new System.Text.StringBuilder();
            var current = ex;
            int depth = 0;

            while (current != null && depth < 5)
            {
                sb.AppendLine($"         {new string('>', depth)} {current.GetType().Name}: {current.Message}");

                var frames = new System.Diagnostics.StackTrace(current, false).GetFrames();
                if (frames != null)
                {
                    int shown = 0;
                    foreach (var frame in frames)
                    {
                        var method = frame.GetMethod();
                        if (method == null) continue;
                        sb.AppendLine($"           {method.DeclaringType?.FullName}.{method.Name}");
                        if (++shown >= 4) break;
                    }
                }

                current = current.InnerException;
                depth++;
            }

            return sb.ToString().TrimEnd();
        }

        /// <summary>构造一个只有私有无参构造函数的窗口（取色器这类）。</summary>
        private static Window PrivateWindow(System.Type type)
        {
            var ctor = type.GetConstructor(
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                null, System.Type.EmptyTypes, null);

            if (ctor == null) throw new InvalidOperationException("找不到私有无参构造函数");
            return (Window)ctor.Invoke(null);
        }

        /// <summary>等待并接管互斥体（旧进程退出时它会变成废弃状态）。</summary>
        private static bool WaitForMutex(Mutex mutex, TimeSpan timeout)
        {
            try
            {
                return mutex.WaitOne(timeout);
            }
            catch (AbandonedMutexException)
            {
                // 旧进程直接退出了，这时互斥体归我们所有
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn("等待单实例互斥体失败：" + ex.Message);
                return false;
            }
        }

        // ==================== 实例激活（兼存活探测）====================

        /// <summary>
        /// 让第二个实例能请求本实例弹出设置面板。
        /// 更重要的是：回执是在界面线程上发出的，所以能收到回执就说明本实例没卡死。
        /// 卡死的残留进程收不到请求（没人监听），也就永远不回执，
        /// 于是新实例会判定它已失效并自行启动 —— 用户不会再被挡在门外。
        /// </summary>
        private void StartActivationListener()
        {
            try
            {
                _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
                _ackEvent = new EventWaitHandle(false, EventResetMode.AutoReset, AckEventName);
                _quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
                _stopListener = new ManualResetEvent(false);

                _listenerThread = new Thread(ActivationLoop)
                {
                    IsBackground = true,
                    Name = "RadialLauncher.Activation",
                };
                _listenerThread.Start();

                Logger.Info("实例激活监听已启动");
            }
            catch (Exception ex)
            {
                Logger.Warn("启动实例激活监听失败：" + ex.Message);
            }
        }

        private void ActivationLoop()
        {
            var activate = _activateEvent;
            var quit = _quitEvent;
            var stop = _stopListener;

            if (activate == null || quit == null || stop == null) return;

            var handles = new WaitHandle[] { activate, quit, stop };

            while (true)
            {
                int index;
                try
                {
                    index = WaitHandle.WaitAny(handles);
                }
                catch
                {
                    return;
                }

                if (index == 1)
                {
                    // 收到 --quit：先回执再退出，让调用方知道请求被受理了
                    try
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            try { _ackEvent?.Set(); } catch { }
                            Logger.Info("收到 --quit 请求，优雅退出");
                            Shutdown();
                        }));
                    }
                    catch
                    {
                        try { _ackEvent?.Set(); } catch { }
                    }
                    return;
                }

                if (index != 0) return;   // 收到停止信号

                try
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            _main?.OpenSettings();
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn("响应激活请求失败：" + ex.Message);
                        }

                        try { _ackEvent?.Set(); } catch { }
                    }));
                }
                catch
                {
                    try { _ackEvent?.Set(); } catch { }
                }
            }
        }

        /// <summary>让已运行的实例退出；返回是否收到了回执。</summary>
        private static bool RequestRunningInstanceQuit()
        {
            try
            {
                using var quit = EventWaitHandle.OpenExisting(QuitEventName);
                using var ack = EventWaitHandle.OpenExisting(AckEventName);

                quit.Set();
                return ack.WaitOne(ActivationTimeoutMs);
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                return false;
            }
            catch (Exception ex)
            {
                Logger.Warn("请求已有实例退出失败：" + ex.Message);
                return false;
            }
        }

        /// <summary>请求已有实例弹出设置面板；收到回执才算它活着。</summary>
        private static bool TryActivateRunningInstance()
        {
            try
            {
                using var activate = EventWaitHandle.OpenExisting(ActivateEventName);
                using var ack = EventWaitHandle.OpenExisting(AckEventName);

                activate.Set();
                return ack.WaitOne(ActivationTimeoutMs);
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // 事件不存在：对方不是这一版，或者已经完全退出
                return false;
            }
            catch (Exception ex)
            {
                Logger.Warn("请求激活已有实例失败：" + ex.Message);
                return false;
            }
        }

        private void StopActivationListener()
        {
            try { _stopListener?.Set(); } catch { }

            try { _activateEvent?.Dispose(); } catch { }
            try { _ackEvent?.Dispose(); } catch { }
            try { _quitEvent?.Dispose(); } catch { }
            try { _stopListener?.Dispose(); } catch { }

            _activateEvent = null;
            _ackEvent = null;
            _quitEvent = null;
            _stopListener = null;
            _listenerThread = null;
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // 先停监听，免得拆窗口的过程中又收到激活请求
            StopActivationListener();

            try
            {
                _main?.Shutdown();
            }
            catch (Exception ex)
            {
                Logger.Error("退出清理失败", ex);
            }

            Logger.Info("================ Radial Launcher 退出 ================");

            _instanceMutex?.Dispose();
            _instanceMutex = null;

            base.OnExit(e);
        }
    }
}
