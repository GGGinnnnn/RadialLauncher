using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using RadialLauncher.Models;

namespace RadialLauncher.Services
{
    /// <summary>
    /// 配置读写。文件位于 %AppData%\RadialLauncher\config.json。
    /// 支持从 v1（平铺字段）自动迁移到 v2（分组字段）。
    /// </summary>
    public sealed class ConfigService
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };

        public static string DataDirectory { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RadialLauncher");

        public static string ConfigPath { get; } = Path.Combine(DataDirectory, "config.json");

        private static string BackupPath => Path.Combine(DataDirectory, "config.backup.json");

        public AppConfig Config { get; private set; } = new();

        public void Load()
        {
            try
            {
                if (!File.Exists(ConfigPath))
                {
                    Config = new AppConfig();
                    Save();
                    Logger.Info("未找到配置文件，已创建默认配置");
                    return;
                }

                var json = File.ReadAllText(ConfigPath);
                var loaded = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);

                if (loaded == null)
                {
                    Logger.Warn("配置文件内容为空，使用默认配置");
                    Config = new AppConfig();
                    return;
                }

                // v1 → v2 迁移。
                // 不能只看 SchemaVersion：v1 文件里根本没有这个字段，
                // 反序列化后会保留属性默认值 2，从而掩盖掉旧格式。
                // 所以以“有没有 Trigger 分组”作为主要判据。
                if (LooksLikeLegacy(json) || loaded.SchemaVersion < 2)
                {
                    Logger.Info($"检测到旧版配置（v{loaded.SchemaVersion}），开始迁移");
                    loaded = Migrate(json, loaded);
                    Config = loaded;
                    Save();
                    Logger.Info("配置迁移完成");
                    return;
                }

                Config = loaded;
                Normalize(Config);
                Logger.Info($"配置已加载：Ctrl={Config.Trigger.ByCtrl} Win={Config.Trigger.ByWin} 中键={Config.Trigger.ByMiddleMouse}");
            }
            catch (Exception ex)
            {
                Logger.Error("读取配置失败，回退到默认配置", ex);
                TryBackupCorruptFile();
                Config = new AppConfig();
            }
        }

        public void Save()
        {
            try
            {
                if (!Directory.Exists(DataDirectory)) Directory.CreateDirectory(DataDirectory);

                Normalize(Config);

                var json = JsonSerializer.Serialize(Config, JsonOptions);

                // 原子写入：先写临时文件再替换，避免写一半断电导致配置损坏
                var tmp = ConfigPath + ".tmp";
                File.WriteAllText(tmp, json);

                if (File.Exists(ConfigPath)) File.Replace(tmp, ConfigPath, BackupPath, true);
                else File.Move(tmp, ConfigPath);

                Logger.Debug("配置已保存");
            }
            catch (Exception ex)
            {
                Logger.Error("保存配置失败", ex);
            }
        }

        /// <summary>给设置面板用的深拷贝，取消时不会污染运行中的配置。</summary>
        public AppConfig CreateWorkingCopy()
        {
            try
            {
                var json = JsonSerializer.Serialize(Config, JsonOptions);
                var copy = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
                if (copy != null) return copy;
            }
            catch (Exception ex)
            {
                Logger.Error("复制配置失败", ex);
            }
            return new AppConfig();
        }

        /// <summary>用编辑好的副本替换当前配置并落盘。</summary>
        public void Replace(AppConfig config)
        {
            Config = config;
            Normalize(Config);
            Save();
        }

        public void ResetToDefaults()
        {
            Config = new AppConfig();
            Save();
            Logger.Info("配置已恢复默认");
        }

        /// <summary>把明显越界的值收敛到合法范围。</summary>
        public static void Normalize(AppConfig c)
        {
            c.SchemaVersion = 2;

            c.Appearance.ItemCount = c.Appearance.ItemCount == 4 ? 4 : 8;
            c.Appearance.Scale = Clamp(c.Appearance.Scale, 0.6, 1.6, 1.0);
            c.Appearance.ItemSize = Clamp(c.Appearance.ItemSize, 44, 120, 74);
            c.Appearance.DeadZone = Clamp(c.Appearance.DeadZone, 0, 120, 34);
            c.Appearance.Opacity = Clamp(c.Appearance.Opacity, 0.3, 1.0, 1.0);

            c.Trigger.HoldThresholdMs = (int)Clamp(c.Trigger.HoldThresholdMs, 60, 1500, 220);

            if (!c.Trigger.ByCtrl && !c.Trigger.ByWin && !c.Trigger.ByMiddleMouse)
                c.Trigger.ByCtrl = true;   // 至少保留一个触发键

            c.System.Language = string.IsNullOrWhiteSpace(c.System.Language) ? "zh-CN" : c.System.Language;
            if (string.IsNullOrWhiteSpace(c.System.GitHubUrl))
                c.System.GitHubUrl = new SystemSettings().GitHubUrl;

            c.Environment ??= new EnvironmentSettings();
            c.Environment.BlacklistProcesses ??= new();
            c.Environment.WhitelistProcesses ??= new();

            c.WheelItems ??= WheelItem.CreateDefaults();
            c.Profiles ??= new();

            foreach (var item in c.WheelItems) NormalizeItem(item);
            foreach (var profile in c.Profiles)
            {
                profile.ProcessNames ??= new();
                profile.TitleKeywords ??= new();
                profile.Items ??= new();
                foreach (var item in profile.Items) NormalizeItem(item);
            }
        }

        private static void NormalizeItem(WheelItem item)
        {
            item.Name ??= "";
            item.Icon ??= "●";
            item.IconPath ??= "";
            item.ActionType = string.IsNullOrWhiteSpace(item.ActionType) ? "Shortcut" : item.ActionType;
            item.ActionValue ??= "";
            item.Arguments ??= "";
            item.WorkingDirectory ??= "";
        }

        private static double Clamp(double value, double min, double max, double fallback)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return fallback;
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private void TryBackupCorruptFile()
        {
            try
            {
                if (!File.Exists(ConfigPath)) return;
                var dest = Path.Combine(DataDirectory,
                    "config.corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json");
                File.Copy(ConfigPath, dest, true);
                Logger.Warn("损坏的配置已备份到 " + dest);
            }
            catch { }
        }

        // ==================== v1 → v2 迁移 ====================

        /// <summary>v1 的配置是平铺字段，没有 Trigger / Appearance 这些分组对象。</summary>
        private static bool LooksLikeLegacy(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;

                return !doc.RootElement.TryGetProperty("Trigger", out _)
                       && !doc.RootElement.TryGetProperty("Appearance", out _);
            }
            catch
            {
                return false;
            }
        }

        private sealed class LegacyConfig
        {
            public bool UseWhitelist { get; set; }
            public System.Collections.Generic.List<string>? WhitelistProcesses { get; set; }
            public System.Collections.Generic.List<string>? BlacklistProcesses { get; set; }
            public bool TriggerByCtrl { get; set; } = true;
            public bool TriggerByWin { get; set; }
            public bool TriggerByMiddleMouse { get; set; }
            public int WheelItemCount { get; set; } = 8;
            public string? BackgroundColor { get; set; }
            public string? BorderColor { get; set; }
            public string? ItemColor { get; set; }
            public string? ItemSelectedColor { get; set; }
            public string? TextColor { get; set; }
            public System.Collections.Generic.List<WheelItem>? WheelItems { get; set; }
        }

        private static AppConfig Migrate(string json, AppConfig alreadyParsed)
        {
            var result = new AppConfig();

            try
            {
                var legacy = JsonSerializer.Deserialize<LegacyConfig>(json, JsonOptions);
                if (legacy == null) return alreadyParsed;

                result.Environment.UseWhitelist = legacy.UseWhitelist;
                result.Environment.WhitelistProcesses = legacy.WhitelistProcesses ?? new();
                result.Environment.BlacklistProcesses = legacy.BlacklistProcesses ?? new();

                result.Trigger.ByCtrl = legacy.TriggerByCtrl;
                result.Trigger.ByWin = legacy.TriggerByWin;
                result.Trigger.ByMiddleMouse = legacy.TriggerByMiddleMouse;
                result.Trigger.Enabled = true;

                result.Appearance.ItemCount = legacy.WheelItemCount == 4 ? 4 : 8;
                if (!string.IsNullOrWhiteSpace(legacy.BackgroundColor)) result.Appearance.BackgroundColor = legacy.BackgroundColor!;
                if (!string.IsNullOrWhiteSpace(legacy.BorderColor)) result.Appearance.BorderColor = legacy.BorderColor!;
                if (!string.IsNullOrWhiteSpace(legacy.ItemColor)) result.Appearance.ItemColor = legacy.ItemColor!;
                if (!string.IsNullOrWhiteSpace(legacy.ItemSelectedColor)) result.Appearance.ItemSelectedColor = legacy.ItemSelectedColor!;
                if (!string.IsNullOrWhiteSpace(legacy.TextColor)) result.Appearance.TextColor = legacy.TextColor!;

                if (legacy.WheelItems is { Count: > 0 })
                {
                    result.WheelItems = legacy.WheelItems;
                    foreach (var item in result.WheelItems)
                        item.ActionValue = MapLegacyShortcut(item.ActionType, item.ActionValue);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("迁移旧配置失败，改用默认配置", ex);
                return result;
            }

            return result;
        }

        /// <summary>旧版用枚举名表示快捷键，新版统一用按键序列。</summary>
        private static string MapLegacyShortcut(string actionType, string value)
        {
            if (!string.Equals(actionType, "Shortcut", StringComparison.OrdinalIgnoreCase)) return value;

            return value switch
            {
                "ShowDesktop" => "Win+D",
                "LockScreen" => "Win+L",
                "Clipboard" => "Win+V",
                "Screenshot" => "Win+Shift+S",
                "TaskManager" => "Ctrl+Shift+Esc",
                "Settings" => "Win+I",
                "Explorer" => "Win+E",
                "Search" => "Win+S",
                _ => value,
            };
        }
    }
}
