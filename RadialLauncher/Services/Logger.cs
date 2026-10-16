using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;

namespace RadialLauncher.Services
{
    public enum LogLevel
    {
        Debug = 0,
        Info = 1,
        Warn = 2,
        Error = 3,
    }

    /// <summary>
    /// 轻量日志。写入数据目录下的 RadialLauncher.log（默认 %AppData%\RadialLauncher，
    /// 便携模式下为 exe 同级目录 —— 见 <see cref="AppPaths"/>），超过 1MB 自动轮转。
    ///
    /// 落盘在**后台线程**完成：调用方（很可能正是全局钩子所在的 UI 线程）只把日志行
    /// 塞进队列就立即返回，绝不做文件 I/O。原来是每次 File.AppendAllText（开→写→关）
    /// 且在同一把锁里同步执行，日志一多就会给 UI 和钩子延迟加码。
    /// 设置面板读日志前会先 Flush()，保证看到的是最新内容。
    /// </summary>
    public static class Logger
    {
        private const long MaxBytes = 1024 * 1024;

        /// <summary>待落盘的日志行。无界队列，写入方永不阻塞。</summary>
        private static readonly BlockingCollection<string> Pending =
            new(new ConcurrentQueue<string>());

        /// <summary>保护 _writerThread / _stream，并让“写入”与“关闭文件流”互斥。</summary>
        private static readonly object LifecycleGate = new();

        private static Thread? _writerThread;
        private static StreamWriter? _stream;

        /// <summary>已入队 / 已落盘的日志行数，用于 Flush 精确等待（避免“队列空了但还没写盘”）。</summary>
        private static long _enqueued;

        private static long _persisted;

        // ==================== 测试模式 ====================

        /// <summary>测试模式下的详细日志文件；未开启时为 null。</summary>
        private static string? _sessionPath;

        private static StreamWriter? _sessionStream;

        /// <summary>本进程累计的警告 / 错误条数，收尾时写进详细日志的总结。</summary>
        private static int _warnCount;
        private static int _errorCount;

        /// <summary>测试模式详细日志的完整路径（未开启返回 null）。</summary>
        public static string? SessionLogPath => _sessionPath;

        /// <summary>测试模式是否已开启。</summary>
        public static bool IsTestMode => _sessionPath != null;

        /// <summary>最低记录级别。默认 Info；可用环境变量 RADIALLAUNCHER_LOGLEVEL 覆盖（如 Debug）。</summary>
        public static LogLevel MinLevel { get; set; } = ResolveDefaultLevel();

        private static LogLevel ResolveDefaultLevel()
        {
            try
            {
                var raw = Environment.GetEnvironmentVariable("RADIALLAUNCHER_LOGLEVEL");
                if (!string.IsNullOrWhiteSpace(raw) &&
                    Enum.TryParse<LogLevel>(raw.Trim(), true, out var parsed))
                {
                    return parsed;
                }
            }
            catch
            {
                // 读环境变量失败就用默认值
            }

            return LogLevel.Info;
        }

        private static string _directoryPath = AppPaths.ResolveDataDirectory();

        /// <summary>
        /// 日志目录 —— 与配置目录始终同一个（由 <see cref="AppPaths"/> 统一裁决）。
        /// 也可以直接赋值重定向：单元测试把它指到临时目录，就不会污染真实日志。
        /// </summary>
        public static string DirectoryPath
        {
            get => _directoryPath;
            set
            {
                if (string.IsNullOrWhiteSpace(value))
                    throw new ArgumentException("日志目录不能为空", nameof(value));

                if (string.Equals(_directoryPath, value, StringComparison.Ordinal)) return;

                lock (LifecycleGate)
                {
                    CloseStreamLocked();   // 换了目录，旧的句柄必须先放掉
                    _directoryPath = value;
                }
            }
        }

        public static string LogPath => Path.Combine(DirectoryPath, "RadialLauncher.log");

        static Logger()
        {
            // 进程退出时尽力把队列里的日志落盘（后台线程可能来不及写完）
            try { AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush(); } catch { }
        }

        /// <summary>有新日志时触发（供设置面板的日志视图使用）。</summary>
        public static event Action<string>? EntryWritten;

        public static void Debug(string message) => Write(LogLevel.Debug, message, null);
        public static void Info(string message) => Write(LogLevel.Info, message, null);
        public static void Warn(string message) => Write(LogLevel.Warn, message, null);

        public static void Error(string message, Exception? ex = null)
            => Write(LogLevel.Error, message, ex);

        private static void Write(LogLevel level, string message, Exception? ex)
        {
            if (level < MinLevel) return;

            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level.ToString().ToUpperInvariant(),-5}] {message}";
            if (ex != null) line += Environment.NewLine + "        " + ex;

            if (level == LogLevel.Warn) Interlocked.Increment(ref _warnCount);
            else if (level == LogLevel.Error) Interlocked.Increment(ref _errorCount);

            try
            {
                EnsureWriter();
                Interlocked.Increment(ref _enqueued);   // 先计数再入队：Flush 不会在入队前沿返回
                Pending.Add(line);                       // 只入队，不做任何 I/O
            }
            catch
            {
                // 日志失败绝不能影响主流程
            }

            try { EntryWritten?.Invoke(line); } catch { }
        }

        private static void EnsureWriter()
        {
            if (_writerThread != null) return;

            lock (LifecycleGate)
            {
                if (_writerThread != null) return;

                _writerThread = new Thread(WriterLoop)
                {
                    IsBackground = true,
                    Name = "RadialLauncher.Logger",
                };
                _writerThread.Start();
            }
        }

        private static void WriterLoop()
        {
            var buffer = new StringBuilder();

            try
            {
                while (!Pending.IsCompleted)
                {
                    string first;
                    try { first = Pending.Take(); }
                    catch (InvalidOperationException) { break; }   // 队列已 CompleteAdding（ObjectDisposedException 也是它的子类）

                    buffer.Clear();
                    buffer.Append(first).Append(Environment.NewLine);
                    int count = 1;

                    // 把已经排队的日志一起取走，合并成一次写盘
                    while (buffer.Length < 32 * 1024 && Pending.TryTake(out var more))
                    {
                        buffer.Append(more).Append(Environment.NewLine);
                        count++;
                    }

                    WriteBatch(buffer.ToString());
                    Interlocked.Add(ref _persisted, count);
                }
            }
            catch
            {
                // 后台线程自身的异常也不能让进程崩掉
            }

            lock (LifecycleGate) CloseStreamLocked();
        }

        private static void WriteBatch(string text)
        {
            lock (LifecycleGate)
            {
                try
                {
                    if (_stream == null)
                    {
                        if (!System.IO.Directory.Exists(DirectoryPath))
                            System.IO.Directory.CreateDirectory(DirectoryPath);

                        // 只在文件尚未打开时轮转，避免“一边写一边挪文件”
                        RotateIfNeeded();

                        var file = new FileStream(
                            LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                        _stream = new StreamWriter(file, new UTF8Encoding(false)) { AutoFlush = false };
                    }

                    _stream.Write(text);
                    _stream.Flush();

                    // 常驻句柄不会自己触发轮转，这里按实际文件大小手动判断
                    if (_stream.BaseStream.Length >= MaxBytes)
                    {
                        CloseStreamLocked();
                        RotateIfNeeded();
                    }
                }
                catch
                {
                    CloseStreamLocked();
                }

                // 测试模式：同一批内容再写一份详细日志
                WriteSessionLocked(text);
            }
        }

        /// <summary>把一批日志追加到测试模式详细日志。调用方必须已持有 LifecycleGate。</summary>
        private static void WriteSessionLocked(string text)
        {
            if (_sessionPath == null) return;

            try
            {
                _sessionStream ??= new StreamWriter(
                    new FileStream(_sessionPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                    new UTF8Encoding(false)) { AutoFlush = false };

                _sessionStream.Write(text);
                _sessionStream.Flush();
            }
            catch
            {
                // 详细日志写不进去也不能影响主流程，下次再试着重开
                try { _sessionStream?.Dispose(); } catch { }
                _sessionStream = null;
            }
        }

        private static void CloseStreamLocked()
        {
            try { _stream?.Dispose(); } catch { }
            _stream = null;
        }

        private static void RotateIfNeeded()
        {
            var info = new FileInfo(LogPath);
            if (!info.Exists || info.Length < MaxBytes) return;

            var backup = LogPath + ".old";
            try
            {
                if (File.Exists(backup)) File.Delete(backup);
                File.Move(LogPath, backup);
            }
            catch
            {
                try { File.WriteAllText(LogPath, ""); } catch { }
            }
        }

        /// <summary>
        /// 等待队列排空并把缓冲落盘。读日志 / 清空日志 / 打开日志文件前调用，
        /// 保证调用方看到的是最新内容。
        /// </summary>
        public static void Flush(int timeoutMs = 1000)
        {
            if (_writerThread == null) return;

            // 等到“已入队的行数 == 已落盘的行数”。
            // 只判断队列是否为空不够：写入线程取走最后一行后队列就空了，
            // 但此刻它还没写盘，读日志会漏掉这一行。
            var deadline = Environment.TickCount64 + timeoutMs;
            while (Interlocked.Read(ref _persisted) < Interlocked.Read(ref _enqueued) &&
                   Environment.TickCount64 < deadline)
            {
                Thread.Sleep(2);
            }

            lock (LifecycleGate)
            {
                try { _stream?.Flush(); } catch { }
            }
        }

        /// <summary>读取最后 N 行，用于日志窗口。</summary>
        public static string ReadTail(int maxLines = 400)
        {
            try
            {
                Flush();
                if (!File.Exists(LogPath)) return "(暂无日志)";

                // 后台写入流是以 FileShare.ReadWrite 打开的，这里也必须允许写，
                // 否则 File.ReadAllLines（默认只允许读）会因共享冲突抛 IOException。
                var lines = ReadAllLinesShared(LogPath);
                int start = Math.Max(0, lines.Length - maxLines);
                var sb = new StringBuilder();
                for (int i = start; i < lines.Length; i++) sb.AppendLine(lines[i]);
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return "(读取日志失败：" + ex.Message + ")";
            }
        }

        private static string[] ReadAllLinesShared(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, Encoding.UTF8);

            var lines = new System.Collections.Generic.List<string>();
            string? line;
            while ((line = reader.ReadLine()) != null) lines.Add(line);
            return lines.ToArray();
        }

        public static void Clear()
        {
            try
            {
                Flush();

                lock (LifecycleGate)
                {
                    CloseStreamLocked();
                    if (File.Exists(LogPath)) File.Delete(LogPath);
                }
            }
            catch { }
        }

        // ==================== 测试模式 ====================

        /// <summary>
        /// 开启测试模式：把本次运行的全部日志（含 Debug）另存为一份详细日志。
        ///
        /// <para>文件名：<c>RadialLauncher_日期_时间_启动参数_权限.log</c>，
        /// 例如 <c>RadialLauncher_2026-10-16_14-30-22_--settings_admin.log</c>。
        /// 启动参数会先做文件名安全化（无参数时写 noargs），权限写 admin / user。</para>
        ///
        /// <para>由设置里的「测试模式」开关或启动参数 <c>--test</c> 触发。
        /// 用 <c>--test</c> 时会在解析参数后立刻调用，所以连互斥体之前的事都能记上。</para>
        /// </summary>
        public static void StartSessionLog(string[]? args, bool elevated)
        {
            if (_sessionPath != null) return;   // 已经开过了

            try
            {
                if (!System.IO.Directory.Exists(DirectoryPath))
                    System.IO.Directory.CreateDirectory(DirectoryPath);

                var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                var name = $"RadialLauncher_{stamp}_{BuildArgsToken(args)}_{(elevated ? "admin" : "user")}.log";
                _sessionPath = Path.Combine(DirectoryPath, name);

                // 详细日志就要详细：从这一刻起 Debug 也记
                MinLevel = LogLevel.Debug;

                WriteSessionHeader(args, elevated);

                Info($"测试模式已开启，详细日志：{_sessionPath}");
            }
            catch (Exception ex)
            {
                _sessionPath = null;
                Warn("开启测试模式失败：" + ex.Message);
            }
        }

        /// <summary>收尾：把警告 / 错误统计追加到详细日志末尾。</summary>
        public static void EndSessionLog()
        {
            if (_sessionPath == null) return;

            try
            {
                Flush();

                var summary =
                    Environment.NewLine +
                    "============================================================" + Environment.NewLine +
                    $" 运行结束 {DateTime.Now:yyyy-MM-dd HH:mm:ss}" + Environment.NewLine +
                    $" 警告 {Volatile.Read(ref _warnCount)} 条 · 错误 {Volatile.Read(ref _errorCount)} 条" + Environment.NewLine +
                    "============================================================" + Environment.NewLine;

                lock (LifecycleGate) WriteSessionLocked(summary);

                try { _sessionStream?.Flush(); } catch { }
            }
            catch { }
        }

        private static void WriteSessionHeader(string[]? args, bool elevated)
        {
            var sb = new StringBuilder();
            void Row(string label, string value) => sb.Append(" ").Append(label.PadRight(12)).Append(": ").Append(value).Append(Environment.NewLine);

            sb.AppendLine("============================================================");
            sb.AppendLine(" Radial Launcher 详细日志（测试模式）");
            sb.AppendLine("============================================================");
            Row("生成时间", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            Row("程序版本", typeof(Logger).Assembly.GetName().Version?.ToString() ?? "未知");
            Row("程序路径", SafeGet(() => Environment.ProcessPath ?? "未知"));
            Row("启动参数", args is { Length: > 0 } ? string.Join(" ", args) : "(无)");
            Row("管理员权限", elevated ? "是" : "否");
            Row("数据目录", DirectoryPath);
            Row("操作系统", SafeGet(() => Environment.OSVersion.VersionString + (Environment.Is64BitOperatingSystem ? " (64 位)" : " (32 位)")));
            Row("运行时", SafeGet(() => Environment.Version.ToString()));
            Row("机器名", SafeGet(() => Environment.MachineName));
            Row("用户名", SafeGet(() => Environment.UserName));
            sb.AppendLine("------------------------------------------------------------");
            sb.AppendLine(" 级别说明：[ERROR] 错误（需要处理）  [WARN ] 警告（可能有问题）");
            sb.AppendLine("            [INFO ] 正常流程      [DEBUG] 细节");
            sb.AppendLine("============================================================");
            sb.AppendLine();

            lock (LifecycleGate) WriteSessionLocked(sb.ToString());
        }

        private static string SafeGet(Func<string> get)
        {
            try { return get(); } catch { return "未知"; }
        }

        /// <summary>把启动参数变成能放进文件名的片段。</summary>
        private static string BuildArgsToken(string[]? args)
        {
            if (args == null || args.Length == 0) return "noargs";

            var parts = new System.Collections.Generic.List<string>();
            foreach (var raw in args)
            {
                var token = (raw ?? "").Trim();
                if (token.Length == 0) continue;

                foreach (var bad in Path.GetInvalidFileNameChars()) token = token.Replace(bad, '_');
                token = token.Replace(' ', '_');
                parts.Add(token);
            }

            if (parts.Count == 0) return "noargs";

            var joined = string.Join("_", parts);

            // 参数太长会把文件名撑爆，截断后加个哈希尾巴保证唯一
            if (joined.Length > 48)
            {
                var hash = 0;
                foreach (var c in joined) hash = hash * 31 + c;
                joined = joined.Substring(0, 40) + "_" + (hash & 0xFFFF).ToString("x4");
            }

            return joined;
        }
    }
}
