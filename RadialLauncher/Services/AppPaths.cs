using System;
using System.IO;

namespace RadialLauncher.Services
{
    /// <summary>数据（配置 + 日志）目录的存放位置。</summary>
    public enum DataDirectoryMode
    {
        /// <summary>%AppData%\RadialLauncher —— 默认，随用户账户保存，多用户互不干扰。</summary>
        AppData = 0,

        /// <summary>可执行文件同级目录 —— 绿色/便携模式，整个文件夹拷走即可带走全部配置。</summary>
        Portable = 1,
    }

    /// <summary>
    /// 数据目录的唯一裁决者。ConfigService 与 Logger 都从这里取目录，避免两处各写一份
    /// 默认路径而逐渐漂移（曾经就是各写一份）。
    ///
    /// 为什么要一个单独的“标记文件”：
    /// 数据目录本身没法记在 config.json 里 —— 那样就成了先有鸡还是先有蛋。
    /// 所以把“用哪个目录”这个决定落成一个与 exe 同级的小标记文件，
    /// 它在读配置之前就能被看到，两种模式也都能找到它。
    ///
    /// 解析优先级（高 → 低）：
    ///   1. 环境变量 RADIALLAUNCHER_DATA_DIR（部署/脚本用的硬覆盖，测试也走这条）
    ///   2. exe 同级存在 portable.marker —— 便携模式
    ///   3. %AppData%\RadialLauncher —— 默认
    /// </summary>
    public static class AppPaths
    {
        /// <summary>便携模式标记文件名（与 exe 同级）。存在即为便携模式。</summary>
        public const string MarkerFileName = "portable.marker";

        /// <summary>硬覆盖数据目录的环境变量名。</summary>
        public const string DataDirectoryEnvVar = "RADIALLAUNCHER_DATA_DIR";

        /// <summary>配置文件名 —— 迁移时要单独照看它（覆盖前先备份）。</summary>
        private const string ConfigFileName = "config.json";

        private static string _executableDirectory = ResolveExecutableDirectory();

        private static string _appDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RadialLauncher");

        /// <summary>
        /// 可执行文件所在目录。默认取宿主目录，可重定向 —— 单元测试把它指到临时目录，
        /// 免得测试往构建产物目录里写便携标记。
        /// </summary>
        public static string ExecutableDirectory
        {
            get => _executableDirectory;
            set
            {
                if (string.IsNullOrWhiteSpace(value))
                    throw new ArgumentException("可执行文件目录不能为空", nameof(value));

                _executableDirectory = Path.TrimEndingDirectorySeparator(value);
            }
        }

        /// <summary>
        /// %AppData%\RadialLauncher。同样可重定向：测试绝不能碰用户真实的 AppData。
        /// </summary>
        public static string AppDataDirectory
        {
            get => _appDataDirectory;
            set
            {
                if (string.IsNullOrWhiteSpace(value))
                    throw new ArgumentException("AppData 目录不能为空", nameof(value));

                _appDataDirectory = Path.TrimEndingDirectorySeparator(value);
            }
        }

        /// <summary>便携标记的完整路径。</summary>
        public static string MarkerPath => Path.Combine(ExecutableDirectory, MarkerFileName);

        /// <summary>当前模式：标记文件存在即便携，否则用 AppData。</summary>
        public static DataDirectoryMode Mode =>
            File.Exists(MarkerPath) ? DataDirectoryMode.Portable : DataDirectoryMode.AppData;

        public static bool IsPortable => Mode == DataDirectoryMode.Portable;

        /// <summary>某个模式对应的目录。</summary>
        public static string DirectoryFor(DataDirectoryMode mode) =>
            mode == DataDirectoryMode.Portable ? ExecutableDirectory : AppDataDirectory;

        /// <summary>
        /// 本次进程实际使用的数据目录。ConfigService / Logger 的静态初值都调这里，
        /// 所以两者永远一致。
        /// </summary>
        public static string ResolveDataDirectory()
        {
            var forced = ReadEnvironmentOverride();
            if (forced != null) return forced;

            return DirectoryFor(Mode);
        }

        /// <summary>是否被 RADIALLAUNCHER_DATA_DIR 硬覆盖（覆盖时模式标记不起作用）。</summary>
        public static bool IsEnvironmentOverrideActive => ReadEnvironmentOverride() != null;

        private static string? ReadEnvironmentOverride()
        {
            try
            {
                var raw = Environment.GetEnvironmentVariable(DataDirectoryEnvVar);
                if (!string.IsNullOrWhiteSpace(raw))
                    return Path.TrimEndingDirectorySeparator(raw.Trim());
            }
            catch
            {
                // 读环境变量失败就退回正常解析
            }

            return null;
        }

        /// <summary>把配置与日志目录一起指向 <paramref name="directory"/>。</summary>
        public static void ApplyDirectory(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
                throw new ArgumentException("数据目录不能为空", nameof(directory));

            var trimmed = Path.TrimEndingDirectorySeparator(directory);

            ConfigService.DataDirectory = trimmed;
            Logger.DirectoryPath = trimmed;   // 换目录会顺手放掉旧的日志句柄
        }

        /// <summary>把配置与日志目录一起切到指定模式对应的目录。</summary>
        public static void Apply(DataDirectoryMode mode) => ApplyDirectory(DirectoryFor(mode));

        // ==================== 切换模式 ====================

        /// <summary>
        /// 切换到指定模式：校验目标可写 → 把现有数据搬过去 → 落/清标记文件 → 切换进程内目录。
        ///
        /// 只做“复制”不删源文件：切错了随时能切回去，代价只是多占一份磁盘。
        /// 成功后会立刻生效（配置与日志都改写到新目录），无需重启。
        /// </summary>
        public static DataDirectorySwitchResult SwitchTo(DataDirectoryMode mode)
        {
            var target = DirectoryFor(mode);
            var current = ConfigService.DataDirectory;

            if (IsSameDirectory(target, current))
                return DataDirectorySwitchResult.Same(target);

            // 标记文件所在目录必须可写，否则“下次启动用哪个目录”这个决定根本存不下来。
            // （装到 Program Files 时，便携目录常常是只读的。）
            bool needCreateMarker = mode == DataDirectoryMode.Portable;
            bool needDeleteMarker = mode == DataDirectoryMode.AppData && File.Exists(MarkerPath);

            if ((needCreateMarker || needDeleteMarker) &&
                !TryEnsureWritable(ExecutableDirectory, out var markerError))
            {
                return DataDirectorySwitchResult.Failed(target,
                    $"无法写入 {ExecutableDirectory}：{markerError}");
            }

            int copied;
            string? previousBackup;
            try
            {
                Directory.CreateDirectory(target);
                (copied, previousBackup) = CopyDataFiles(current, target);
            }
            catch (Exception ex)
            {
                Logger.Error("切换数据目录失败", ex);
                return DataDirectorySwitchResult.Failed(target, ex.Message);
            }

            try
            {
                if (needCreateMarker) WriteMarker();
                else if (needDeleteMarker) File.Delete(MarkerPath);
            }
            catch (Exception ex)
            {
                Logger.Error("写入数据目录标记失败", ex);
                return DataDirectorySwitchResult.Failed(target, ex.Message);
            }

            Apply(mode);

            Logger.Info($"数据目录已切换到 {target}（模式：{DescribeMode(mode)}，迁移 {copied} 个文件）");

            return DataDirectorySwitchResult.Succeeded(target, copied, previousBackup);
        }

        /// <summary>供日志/界面显示的简短模式名。</summary>
        public static string DescribeMode(DataDirectoryMode mode) =>
            mode == DataDirectoryMode.Portable ? "便携（exe 同级）" : "%AppData%";

        // ==================== 内部实现 ====================

        private static void WriteMarker()
        {
            // 内容写成人能看懂的一行，用户翻到它时不会一头雾水
            File.WriteAllText(MarkerPath,
                "此文件表示 Radial Launcher 以“便携模式”运行：配置与日志都保存在本目录。" +
                Environment.NewLine +
                "删除本文件即恢复为使用 %AppData%\\RadialLauncher。" + Environment.NewLine);
        }

        /// <summary>
        /// 把 <paramref name="from"/> 里的数据文件复制到 <paramref name="to"/>。
        /// 目标已有 config.json 时先另存一份，避免“切过去”就把那边的设置冲掉。
        /// </summary>
        private static (int Copied, string? PreviousBackup) CopyDataFiles(string from, string to)
        {
            if (!Directory.Exists(from)) return (0, null);

            string? backup = null;

            var targetConfig = Path.Combine(to, ConfigFileName);
            if (File.Exists(targetConfig))
            {
                backup = Path.Combine(to, $"config.switched-{DateTime.Now:yyyyMMdd-HHmmss}.json");
                File.Copy(targetConfig, backup, true);
                Logger.Warn("目标目录已有配置，已备份为 " + backup);
            }

            int copied = 0;
            foreach (var file in Directory.GetFiles(from))
            {
                var name = Path.GetFileName(file);
                if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;   // 半成品不搬

                try
                {
                    File.Copy(file, Path.Combine(to, name), true);
                    copied++;
                }
                catch (Exception ex)
                {
                    // 单个文件失败（例如日志正被别的进程占着）不该让整体切换失败
                    Logger.Warn($"迁移 {name} 到新数据目录失败：{ex.Message}");
                }
            }

            return (copied, backup);
        }

        /// <summary>目录不存在就建，并试写一个临时文件确认真的可写。</summary>
        private static bool TryEnsureWritable(string directory, out string error)
        {
            error = "";

            try
            {
                if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }

            var probe = Path.Combine(directory, ".write-test-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllText(probe, "");
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                try { if (File.Exists(probe)) File.Delete(probe); } catch { }
            }
        }

        private static bool IsSameDirectory(string a, string b)
        {
            try
            {
                return string.Equals(
                    Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            }
        }

        private static string ResolveExecutableDirectory()
        {
            try
            {
                var baseDirectory = AppContext.BaseDirectory;
                if (!string.IsNullOrWhiteSpace(baseDirectory))
                    return Path.TrimEndingDirectorySeparator(baseDirectory);
            }
            catch
            {
                // 拿不到就退回进程路径
            }

            try
            {
                var processPath = Environment.ProcessPath;
                var directory = string.IsNullOrWhiteSpace(processPath) ? null : Path.GetDirectoryName(processPath);
                if (!string.IsNullOrWhiteSpace(directory))
                    return Path.TrimEndingDirectorySeparator(directory!);
            }
            catch
            {
                // 再不行就当前工作目录
            }

            return Path.TrimEndingDirectorySeparator(Environment.CurrentDirectory);
        }
    }

    /// <summary>切换数据目录的结果。</summary>
    public sealed class DataDirectorySwitchResult
    {
        public bool Success { get; private init; }

        /// <summary>本来就已经是这个模式，什么都没做。</summary>
        public bool AlreadyInMode { get; private init; }

        /// <summary>迁移过去的文件数。</summary>
        public int FilesCopied { get; private init; }

        /// <summary>目标目录（成功后即当前生效目录）。</summary>
        public string TargetDirectory { get; private init; } = "";

        /// <summary>目标目录里原有配置的备份路径（如果发生过覆盖）。</summary>
        public string? PreviousConfigBackup { get; private init; }

        /// <summary>失败原因（人话）。</summary>
        public string? Error { get; private init; }

        private DataDirectorySwitchResult() { }

        internal static DataDirectorySwitchResult Succeeded(string target, int copied, string? backup) => new()
        {
            Success = true,
            TargetDirectory = target,
            FilesCopied = copied,
            PreviousConfigBackup = backup,
        };

        internal static DataDirectorySwitchResult Same(string target) => new()
        {
            Success = true,
            AlreadyInMode = true,
            TargetDirectory = target,
        };

        internal static DataDirectorySwitchResult Failed(string target, string error) => new()
        {
            Success = false,
            TargetDirectory = target,
            Error = error,
        };
    }
}
