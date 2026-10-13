using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using RadialLauncher.Models;

namespace RadialLauncher.Services
{
    /// <summary>执行结果，供界面提示用。</summary>
    public sealed class ActionResult
    {
        public bool Success { get; init; }
        public string Message { get; init; } = "";
        public string Detail { get; init; } = "";

        public static ActionResult Ok(string message = "") => new() { Success = true, Message = message };
        public static ActionResult Fail(string message, string detail = "") => new() { Success = false, Message = message, Detail = detail };
    }

    /// <summary>把 WheelItem 变成一个真实动作。</summary>
    public sealed class ActionExecutor
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHEmptyRecycleBinW(IntPtr hwnd, string? pszRootPath, uint dwFlags);

        [DllImport("powrprof.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

        private const uint SHERB_NOCONFIRMATION = 0x00000001;
        private const uint SHERB_NOPROGRESSUI = 0x00000002;
        private const uint SHERB_NOSOUND = 0x00000004;

        public ActionResult Execute(WheelItem item)
        {
            if (item == null) return ActionResult.Fail("空的配置项");

            try
            {
                return item.ActionType switch
                {
                    "LaunchApp" => LaunchApp(item),
                    "OpenFolder" => OpenFolder(item),
                    "OpenUrl" => OpenUrl(item),
                    "Shortcut" => RunShortcut(item),
                    "Command" => RunCommand(item),
                    "Script" => RunScript(item),
                    "Builtin" => RunBuiltin(item),
                    _ => ActionResult.Fail($"未知的功能类型：{item.ActionType}"),
                };
            }
            catch (Exception ex)
            {
                Logger.Error($"执行「{item.Name}」失败", ex);
                return ActionResult.Fail(ex.Message, ex.ToString());
            }
        }

        // ==================== 启动应用 ====================

        private ActionResult LaunchApp(WheelItem item)
        {
            var target = Expand(item.ActionValue);
            if (string.IsNullOrWhiteSpace(target))
                return ActionResult.Fail(Loc.T("Msg_NotConfigured"));

            var args = Expand(item.Arguments);
            var workDir = Expand(item.WorkingDirectory);

            if (item.RunAsAdmin)
            {
                bool ok = ElevationService.StartElevated(target, args, NullIfEmpty(workDir));
                return ok ? ActionResult.Ok() : ActionResult.Fail("提权启动被取消或失败");
            }

            var psi = new ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true,
            };

            if (!string.IsNullOrWhiteSpace(args)) psi.Arguments = args;
            if (!string.IsNullOrWhiteSpace(workDir) && Directory.Exists(workDir)) psi.WorkingDirectory = workDir;

            Process.Start(psi);
            Logger.Info($"已启动应用：{target} {args}");
            return ActionResult.Ok();
        }

        // ==================== 打开文件夹 ====================

        private ActionResult OpenFolder(WheelItem item)
        {
            var path = Expand(item.ActionValue);

            if (string.IsNullOrWhiteSpace(path))
            {
                // 没配置就打开“此电脑”
                Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
                return ActionResult.Ok();
            }

            if (!Directory.Exists(path) && !File.Exists(path))
                return ActionResult.Fail(string.Format(Loc.T("Msg_FileNotFound"), path));

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = true,
            });

            Logger.Info($"已打开文件夹：{path}");
            return ActionResult.Ok();
        }

        // ==================== 打开网址 ====================

        private ActionResult OpenUrl(WheelItem item)
        {
            var url = Expand(item.ActionValue);
            if (string.IsNullOrWhiteSpace(url))
                return ActionResult.Fail(Loc.T("Msg_NotConfigured"));

            if (!url.Contains("://", StringComparison.Ordinal) && !url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                url = "https://" + url;

            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return ActionResult.Ok();
        }

        // ==================== Windows 快捷键 ====================

        private ActionResult RunShortcut(WheelItem item)
        {
            var keys = item.ActionValue;
            if (string.IsNullOrWhiteSpace(keys))
                return ActionResult.Fail(Loc.T("Msg_NotConfigured"));

            // 诊断：记录发送瞬间还按着的修饰键。正常应该是“无”，
            // 若这里出现 Ctrl / Win，说明触发键还没抬起来，会把快捷键带偏。
            Logger.Debug($"发送快捷键前修饰键状态：{KeyStateHelper.DescribeDown()}");

            if (!KeySequence.SendText(keys, out var error))
                return ActionResult.Fail(string.Format(Loc.T("Msg_InvalidShortcut"), error));

            Logger.Info($"已发送快捷键：{keys}");
            return ActionResult.Ok();
        }

        // ==================== 执行命令 ====================

        private ActionResult RunCommand(WheelItem item)
        {
            var command = Expand(item.ActionValue);
            if (string.IsNullOrWhiteSpace(command))
                return ActionResult.Fail(Loc.T("Msg_NotConfigured"));

            var workDir = Expand(item.WorkingDirectory);
            var args = Expand(item.Arguments);

            if (!string.IsNullOrWhiteSpace(args))
                command = command + " " + args;

            if (item.RunAsAdmin)
            {
                bool ok = ElevationService.RunCommandElevated(command, NullIfEmpty(workDir));
                return ok ? ActionResult.Ok() : ActionResult.Fail("提权执行被取消或失败");
            }

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c " + command,
                UseShellExecute = true,
            };

            if (item.HiddenWindow) psi.WindowStyle = ProcessWindowStyle.Hidden;
            if (!string.IsNullOrWhiteSpace(workDir) && Directory.Exists(workDir)) psi.WorkingDirectory = workDir;

            Process.Start(psi);
            Logger.Info($"已执行命令：{command}");
            return ActionResult.Ok();
        }

        // ==================== 运行脚本 ====================

        private ActionResult RunScript(WheelItem item)
        {
            var scriptPath = Expand(item.ActionValue);
            if (string.IsNullOrWhiteSpace(scriptPath))
                return ActionResult.Fail(Loc.T("Msg_NotConfigured"));

            if (!File.Exists(scriptPath))
                return ActionResult.Fail(string.Format(Loc.T("Msg_FileNotFound"), scriptPath));

            var workDir = Expand(item.WorkingDirectory);
            if (string.IsNullOrWhiteSpace(workDir)) workDir = Path.GetDirectoryName(scriptPath) ?? "";

            var args = Expand(item.Arguments);
            var ext = Path.GetExtension(scriptPath).ToLowerInvariant();

            var psi = new ProcessStartInfo
            {
                UseShellExecute = true,
            };

            switch (ext)
            {
                case ".ps1":
                    psi.FileName = "powershell.exe";
                    psi.Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" {args}".Trim();
                    break;

                case ".bat":
                case ".cmd":
                    psi.FileName = "cmd.exe";
                    psi.Arguments = $"/c \"\"{scriptPath}\" {args}\"".Trim();
                    break;

                case ".py":
                    psi.FileName = "python.exe";
                    psi.Arguments = $"\"{scriptPath}\" {args}".Trim();
                    break;

                default:
                    psi.FileName = scriptPath;
                    psi.Arguments = args;
                    break;
            }

            if (item.HiddenWindow) psi.WindowStyle = ProcessWindowStyle.Hidden;
            if (!string.IsNullOrWhiteSpace(workDir) && Directory.Exists(workDir)) psi.WorkingDirectory = workDir;

            if (item.RunAsAdmin)
            {
                bool ok = ElevationService.StartElevated(psi.FileName, psi.Arguments, NullIfEmpty(workDir));
                return ok ? ActionResult.Ok() : ActionResult.Fail("提权执行被取消或失败");
            }

            Process.Start(psi);
            Logger.Info($"已运行脚本：{scriptPath}");
            return ActionResult.Ok();
        }

        // ==================== 内置功能 ====================

        private ActionResult RunBuiltin(WheelItem item)
        {
            var id = item.ActionValue?.Trim() ?? "";
            if (string.IsNullOrEmpty(id)) return ActionResult.Fail(Loc.T("Msg_NotConfigured"));

            switch (id)
            {
                case "LockScreen":
                    NativeMethods.LockWorkStation();
                    return ActionResult.Ok();

                case "Sleep":
                    SetSuspendState(false, false, false);
                    return ActionResult.Ok();

                case "Hibernate":
                    SetSuspendState(true, false, false);
                    return ActionResult.Ok();

                case "Shutdown":
                    return RunShell("shutdown.exe", "/s /t 0");

                case "Restart":
                    return RunShell("shutdown.exe", "/r /t 0");

                case "LogOff":
                    return RunShell("shutdown.exe", "/l");

                case "TaskManager":
                    return RunShell("taskmgr.exe", "");

                case "OpenSettings":
                    return RunShell("ms-settings:", "");

                case "EmptyRecycleBin":
                    SHEmptyRecycleBinW(IntPtr.Zero, null,
                        SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
                    return ActionResult.Ok();

                case "OpenDataFolder":
                    Directory.CreateDirectory(ConfigService.DataDirectory);
                    return RunShell("explorer.exe", $"\"{ConfigService.DataDirectory}\"");

                case "OpenTerminal":
                    return OpenTerminal();

                case "OpenDownloads":
                    return OpenKnownFolder(Environment.SpecialFolder.UserProfile, "Downloads");

                case "OpenDocuments":
                    return RunShell("explorer.exe", $"\"{Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)}\"");

                case "OpenDesktopDir":
                    return RunShell("explorer.exe", $"\"{Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)}\"");

                // 依赖合成快捷键的功能统一查目录里的 Keys，避免把内部 id 当按键文本用。
                case "ShowDesktop":
                case "OpenExplorer":
                case "PlayPause":
                case "NextTrack":
                case "PrevTrack":
                case "StopMedia":
                case "VolumeUp":
                case "VolumeDown":
                case "Mute":
                case "Clipboard":
                case "Screenshot":
                case "EmojiPanel":
                case "Search":
                case "RunDialog":
                    return SendBuiltinKeys(id);

                default:
                    return ActionResult.Fail($"未知的内置功能：{id}");
            }
        }

        /// <summary>
        /// 执行一个"靠合成快捷键"实现的内置功能。
        /// 按键序列来自 <see cref="BuiltinCatalog"/>，保证 id 与按键文本的对应关系只有一处定义。
        /// </summary>
        private static ActionResult SendBuiltinKeys(string id)
        {
            var keys = BuiltinCatalog.KeysFor(id);
            if (string.IsNullOrWhiteSpace(keys))
            {
                // 目录漏配了 Keys，或该功能标识有误。明确报错好过静默发一堆乱键。
                Logger.Warn($"内置功能「{id}」在目录里没有定义按键序列");
                return ActionResult.Fail($"内置功能未配置快捷键：{id}");
            }

            if (!KeySequence.SendText(keys, out var error))
            {
                Logger.Warn($"内置功能「{id}」的按键「{keys}」无法解析：{error}");
                return ActionResult.Fail(string.Format(Loc.T("Msg_InvalidShortcut"), error));
            }

            return ActionResult.Ok();
        }

        private ActionResult OpenKnownFolder(Environment.SpecialFolder root, string sub)
        {
            var path = Path.Combine(Environment.GetFolderPath(root), sub);
            if (!Directory.Exists(path))
                return ActionResult.Fail(string.Format(Loc.T("Msg_FileNotFound"), path));

            return RunShell("explorer.exe", $"\"{path}\"");
        }

        /// <summary>优先 Windows 终端，不存在则退回 cmd。</summary>
        private static ActionResult OpenTerminal()
        {
            foreach (var candidate in new[] { "wt.exe", "cmd.exe" })
            {
                try
                {
                    Process.Start(new ProcessStartInfo(candidate) { UseShellExecute = true });
                    return ActionResult.Ok();
                }
                catch (Exception ex)
                {
                    Logger.Debug($"启动 {candidate} 失败：{ex.Message}");
                }
            }

            return ActionResult.Fail("找不到可用的终端程序");
        }

        private ActionResult RunShell(string fileName, string arguments)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = true,
            };
            if (!string.IsNullOrWhiteSpace(arguments)) psi.Arguments = arguments;

            Process.Start(psi);
            return ActionResult.Ok();
        }

        // ==================== 工具 ====================

        private static string Expand(string? value)
            => string.IsNullOrWhiteSpace(value) ? "" : Environment.ExpandEnvironmentVariables(value.Trim());

        private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
