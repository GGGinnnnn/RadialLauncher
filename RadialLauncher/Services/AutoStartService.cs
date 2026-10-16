using System;
using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;

namespace RadialLauncher.Services
{
    /// <summary>
    /// 开机自启动（当前用户注册表 Run 项，无需管理员权限）。
    ///
    /// <para>**“登记了”不等于“能启动”** —— 这是本类最容易被写错的一点。
    /// 一个自启项要真正生效，必须同时满足：</para>
    /// <list type="number">
    ///   <item>Run 项存在；</item>
    ///   <item>它指向的 exe **还在**（程序被移动、改名、删掉，或随着版本升级换了文件名，
    ///         登记项就成了死路径 —— 开机时 Windows 找不到文件，静默什么都不发生）；</item>
    ///   <item>没有被任务管理器禁用（那会在 StartupApproved 里留下一个“已禁用”标记，
    ///         此时 Run 项明明还在，系统却不会启动它）。</item>
    /// </list>
    /// <para>只判第 1 条就会得到最糟的结果：界面一直显示“已开启”，开机却毫无反应，
    /// 用户还以为是自己没设对。所以 <see cref="IsEnabled"/> 三条都要查。</para>
    /// </summary>
    public static class AutoStartService
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "RadialLauncher";

        /// <summary>
        /// 任务管理器“启动”页把用户禁用/启用的状态存在这里。
        /// 值名与 Run 项同名，REG_BINARY：首字节 2 = 启用，其它（3 / 6）= 已禁用。
        /// </summary>
        private const string ApprovedKey =
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

        /// <summary>StartupApproved 里代表“已启用”的首字节。</summary>
        private const byte ApprovedEnabled = 0x02;

        /// <summary>登记的自启动命令行（原样，含引号与参数）；没登记返回 null。</summary>
        public static string? GetRegisteredCommand()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
                return key?.GetValue(ValueName) as string;
            }
            catch (Exception ex)
            {
                Logger.Error("读取自启动项失败", ex);
                return null;
            }
        }

        /// <summary>登记项里的可执行文件路径（去掉外层引号与后面的参数）；解析不出来返回 null。</summary>
        public static string? GetRegisteredExecutable() => ParseExecutable(GetRegisteredCommand());

        /// <summary>
        /// 从 Run 项的命令行里取出可执行文件路径 —— **纯函数，可单独测试**。
        ///
        /// <para>两种情况都要处理，且都不能按空格切：路径本身可能含空格（还有
        /// <c>C:\Program Files\…</c> 这种必然含空格的）。</para>
        /// <list type="bullet">
        ///   <item>带引号：<c>"C:\a b\app.exe" --args</c> → 取到配对引号为止；</item>
        ///   <item>不带引号：<c>C:\a b\app.exe --args</c> → 取到第一个 <c>.exe</c> 为止。</item>
        /// </list>
        /// </summary>
        public static string? ParseExecutable(string? command)
        {
            if (string.IsNullOrWhiteSpace(command)) return null;

            var text = command.Trim();

            if (text.StartsWith('"'))
                return text.Length > 1 && text.IndexOf('"', 1) is int end && end > 1
                    ? text[1..end]
                    : null;

            int exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            return exe >= 0 ? text[..(exe + 4)] : text;
        }

        /// <summary>登记项是否被任务管理器标记为“已禁用”。</summary>
        public static bool IsDisabledInTaskManager()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(ApprovedKey, false);
                if (key?.GetValue(ValueName) is not byte[] { Length: > 0 } state) return false;
                return state[0] != ApprovedEnabled;
            }
            catch (Exception ex)
            {
                Logger.Error("读取自启动启用状态失败", ex);
                return false;
            }
        }

        /// <summary>
        /// 自启是否**真的会生效**：登记项存在、指向的文件还在、且没被任务管理器禁用。
        ///
        /// <para>给界面用，所以判“能不能启动”而不是“有没有登记” —— 否则会出现
        /// “勾着已开启、开机却没反应”的假象。</para>
        /// </summary>
        public static bool IsEnabled()
        {
            var exe = GetRegisteredExecutable();
            if (string.IsNullOrWhiteSpace(exe)) return false;

            try
            {
                if (!System.IO.File.Exists(exe))
                {
                    Logger.Warn($"自启动项指向的文件已不存在，视为未生效：{exe}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("校验自启动项路径失败", ex);
                return false;
            }

            if (IsDisabledInTaskManager())
            {
                Logger.Warn("自启动项已被任务管理器禁用，视为未生效");
                return false;
            }

            return true;
        }

        /// <summary>
        /// 登记项是否已经指向**当前正在运行的这个程序**。
        /// 换版本（下载了 v0.6、旧的 v0.5 还在）时不满足 —— 该重写，否则开机会启动旧版本。
        /// </summary>
        public static bool PointsToCurrentExecutable()
        {
            try
            {
                var registered = GetRegisteredExecutable();
                var current = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(registered) || string.IsNullOrWhiteSpace(current)) return false;

                // GetFullPath 对畸形路径（非法字符等）会抛。本方法在启动路径上被调用，
                // 绝不能因为注册表里一条坏数据就把程序搞崩 —— 判为“不是当前程序”即可，
                // 上层随后会把它重写成正确值。
                return string.Equals(
                    System.IO.Path.GetFullPath(registered),
                    System.IO.Path.GetFullPath(current),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Logger.Warn("比较自启动项路径失败：" + ex.Message);
                return false;
            }
        }

        public static bool SetEnabled(bool enabled)
        {
            try
            {
                return enabled ? Enable() : Disable();
            }
            catch (Exception ex)
            {
                Logger.Error("设置自启动失败", ex);
                return false;
            }
        }

        private static bool Enable()
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                Logger.Warn("无法确定可执行文件路径，自启动未设置");
                return false;
            }

            // ★ 判断“要不要写”的依据是**内容**，不是 IsEnabled()。
            //   只看 IsEnabled() 会被死路径/旧版本/任务管理器禁用这三种情况骗过去，
            //   于是永远不重写，自启就此静默失效（本次修的正是这个）。
            if (IsEnabled() && PointsToCurrentExecutable())
            {
                // 已经正确登记且生效，什么都不用做（避免每次保存设置都写注册表）
                return true;
            }

            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (key == null) return false;

            key.SetValue(ValueName, $"\"{exe}\"");

            // 之前被任务管理器禁用过的，这里一并解除禁用 —— 否则 Run 项写对了，
            // 系统仍然不会启动它。
            ClearTaskManagerDisabled();

            Logger.Info("已开启开机自启动：" + exe);
            return true;
        }

        private static bool Disable()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                // ★ 这里同样不能依赖 IsEnabled() 提前返回：死路径会让 IsEnabled() 为 false，
                //   于是“关掉自启”永远删不掉那条残留项。
                if (key?.GetValue(ValueName) == null && !IsDisabledInTaskManager()) return true;

                key?.DeleteValue(ValueName, false);
            }

            // Run 项删了，任务管理器那条同名状态就成了孤儿。一并删掉，
            // 免得多留一份“已禁用”痕迹（Enable 里也会清，但别留垃圾）。
            RemoveTaskManagerState();

            Logger.Info("已关闭开机自启动");
            return true;
        }

        /// <summary>删掉任务管理器留下的同名状态值（关闭自启时的清理）。</summary>
        private static void RemoveTaskManagerState()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(ApprovedKey, true);
                key?.DeleteValue(ValueName, false);
            }
            catch (Exception ex)
            {
                Logger.Warn("清理自启动状态失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 清掉任务管理器留下的“已禁用”标记，让 Run 项重新生效。
        /// 写入标准的“已启用”状态（首字节 2，其后清零），与任务管理器勾选“启用”等价。
        /// </summary>
        private static void ClearTaskManagerDisabled()
        {
            try
            {
                if (!IsDisabledInTaskManager()) return;

                using var key = Registry.CurrentUser.CreateSubKey(ApprovedKey, true);
                if (key == null) return;

                key.SetValue(ValueName, new byte[12] { ApprovedEnabled, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
                    RegistryValueKind.Binary);
                Logger.Info("已解除任务管理器对自启动的禁用");
            }
            catch (Exception ex)
            {
                // 不是致命错误：Run 项本身已经写好了
                Logger.Warn("解除任务管理器禁用失败：" + ex.Message);
            }
        }

        /// <summary>当前登录用户是否已经在管理员组里（决定“以管理员运行”是否可用）。</summary>
        public static bool IsUserInAdminGroup()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>管理员提权相关操作。</summary>
    public static class ElevationService
    {
        /// <summary>当前进程是否已提权。</summary>
        public static bool IsElevated
        {
            get
            {
                try
                {
                    using var identity = WindowsIdentity.GetCurrent();
                    return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>当前登录用户是否在管理员组里（决定能否提权）。</summary>
        public static bool IsUserInAdminGroup() => AutoStartService.IsUserInAdminGroup();

        /// <summary>以管理员身份重新启动自己。成功返回 true（调用方应立即退出）。</summary>
        public static bool RestartElevated()
        {
            // 带上 --relaunch，新进程才知道要等旧进程让出单实例互斥体
            return StartElevated(Environment.ProcessPath ?? "", App.RelaunchArgument, null);
        }

        /// <summary>以管理员身份运行某个程序。</summary>
        public static bool StartElevated(string fileName, string arguments, string? workingDirectory)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(fileName)) return false;

                var psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments ?? "",
                    UseShellExecute = true,
                    Verb = "runas",
                };

                if (!string.IsNullOrWhiteSpace(workingDirectory))
                    psi.WorkingDirectory = workingDirectory!;

                Process.Start(psi);
                return true;
            }
            catch (Exception ex)
            {
                // 用户在 UAC 弹窗点了“否”也会走到这里
                Logger.Warn("提权启动失败或被取消：" + ex.Message);
                return false;
            }
        }

        /// <summary>以管理员身份执行一条命令行。</summary>
        public static bool RunCommandElevated(string command, string? workingDirectory)
        {
            if (string.IsNullOrWhiteSpace(command)) return false;
            return StartElevated("cmd.exe", "/k " + command, workingDirectory);
        }
    }
}
