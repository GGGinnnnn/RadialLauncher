using System;
using System.IO;
using System.Text;

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
    /// 轻量日志。写入 %AppData%\RadialLauncher\RadialLauncher.log，超过 1MB 自动截断。
    /// </summary>
    public static class Logger
    {
        private const long MaxBytes = 1024 * 1024;

        private static readonly object Gate = new();

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

        public static string DirectoryPath { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RadialLauncher");

        public static string LogPath { get; } = Path.Combine(DirectoryPath, "RadialLauncher.log");

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
                lock (Gate)
                {
                    if (!System.IO.Directory.Exists(DirectoryPath))
                        System.IO.Directory.CreateDirectory(DirectoryPath);

                    RotateIfNeeded();
                    File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch
            {
                // 日志失败绝不能影响主流程
            }

            try { EntryWritten?.Invoke(line); } catch { }
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

        /// <summary>读取最后 N 行，用于日志窗口。</summary>
        public static string ReadTail(int maxLines = 400)
        {
            try
            {
                if (!File.Exists(LogPath)) return "(暂无日志)";

                var lines = File.ReadAllLines(LogPath);
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

        public static void Clear()
        {
            try
            {
                lock (Gate)
                {
                    if (File.Exists(LogPath)) File.Delete(LogPath);
                }
            }
            catch { }
        }
    }
}
