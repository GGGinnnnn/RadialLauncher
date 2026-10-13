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
    /// 轻量日志。默认写入 %AppData%\RadialLauncher\RadialLauncher.log，超过 1MB 自动轮转。
    ///
    /// 落盘在**后台线程**完成：调用方（很可能正是全局钩子所在的 UI 线程）只把日志行
    /// 塞进队列就立即返回，绝不做文件 I/O。原来是每次 File.AppendAllText（开→写→关）
    /// 且在同一把锁里同步执行，日志一多就会给 UI 和钩子延迟加码。
    /// 设置面板读日志前会先 Flush()，保证看到的是最新内容。
    /// </summary>
    public static class Logger
    {
        private const long MaxBytes = 1024 * 1024;

        /// <summary>覆盖日志目录的环境变量名，与 ConfigService 保持一致。</summary>
        private const string DataDirectoryEnvVar = "RADIALLAUNCHER_DATA_DIR";

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

        private static string _directoryPath = ResolveDefaultDirectory();

        /// <summary>
        /// 日志目录。默认 %AppData%\RadialLauncher，可用环境变量 RADIALLAUNCHER_DATA_DIR
        /// 覆盖，也可以直接赋值重定向 —— 单元测试把它指到临时目录，就不会污染真实日志。
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

        private static string ResolveDefaultDirectory()
        {
            try
            {
                var raw = Environment.GetEnvironmentVariable(DataDirectoryEnvVar);
                if (!string.IsNullOrWhiteSpace(raw)) return raw.Trim();
            }
            catch
            {
                // 读环境变量失败就退回默认目录
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RadialLauncher");
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
    }
}
