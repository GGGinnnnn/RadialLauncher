using System;
using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;

namespace RadialLauncher.Services
{
    /// <summary>开机自启动（当前用户注册表 Run 项，无需管理员权限）。</summary>
    public static class AutoStartService
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "RadialLauncher";

        public static bool IsEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
                return key?.GetValue(ValueName) != null;
            }
            catch (Exception ex)
            {
                Logger.Error("读取自启动项失败", ex);
                return false;
            }
        }

        public static bool SetEnabled(bool enabled)
        {
            try
            {
                // 值和现状一致就什么都不做，避免每次预览设置都去写注册表
                if (IsEnabled() == enabled) return true;

                using var key = Registry.CurrentUser.OpenSubKey(RunKey, true)
                                ?? Registry.CurrentUser.CreateSubKey(RunKey);
                if (key == null) return false;

                if (enabled)
                {
                    var exe = Environment.ProcessPath;
                    if (string.IsNullOrEmpty(exe))
                    {
                        Logger.Warn("无法确定可执行文件路径，自启动未设置");
                        return false;
                    }
                    key.SetValue(ValueName, $"\"{exe}\"");
                    Logger.Info("已开启开机自启动：" + exe);
                }
                else
                {
                    key.DeleteValue(ValueName, false);
                    Logger.Info("已关闭开机自启动");
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.Error("设置自启动失败", ex);
                return false;
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
