using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using RadialLauncher.Models;

namespace RadialLauncher.Services
{
    /// <summary>
    /// 配置读写。文件位于数据目录下的 config.json（默认 %AppData%\RadialLauncher，
    /// 也可切到 exe 同级目录 —— 见 <see cref="AppPaths"/>）。
    /// 支持从 v1（平铺字段）自动迁移到 v2（分组字段）。
    /// </summary>
    public sealed class ConfigService
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };

        // 默认目录不在这里自己拼：统一由 AppPaths 裁决，否则和 Logger 各写一份，
        // 迟早会出现“配置在新目录、日志还在旧目录”这种对不上的情况。
        private static string _dataDirectory = AppPaths.ResolveDataDirectory();

        /// <summary>
        /// 配置所在目录。默认取自 <see cref="AppPaths.ResolveDataDirectory"/>，
        /// 也可以直接赋值重定向 —— 单元测试把它指到临时目录，
        /// 就不会再读写用户真实的 config.json。
        /// </summary>
        public static string DataDirectory
        {
            get => _dataDirectory;
            set
            {
                if (string.IsNullOrWhiteSpace(value))
                    throw new ArgumentException("数据目录不能为空", nameof(value));
                _dataDirectory = value;
            }
        }

        public static string ConfigPath => Path.Combine(DataDirectory, "config.json");

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
                    // 不能用 loaded.SchemaVersion 打日志：它是反序列化后的属性默认值(2)，
                    // 而 v1 文件里根本没有这个键，照它打印会把版本号永远说成 v2。
                    // 要拿真实版本只能回到 JSON 里看有没有这个字段，没有就按 v1 处理。
                    int? declaredVersion = ReadSchemaVersion(json);
                    Logger.Info(declaredVersion is int v
                        ? $"检测到旧版配置（v{v}），开始迁移"
                        : "检测到旧版扁平配置（无 SchemaVersion 字段，按 v1 处理），开始迁移");

                    loaded = Migrate(json, loaded);
                    Config = loaded;
                    Save();
                    Logger.Info("配置迁移完成");
                    return;
                }

                Config = loaded;
                Normalize(Config);
                Logger.Info($"配置已加载：Ctrl={Config.Trigger.ByCtrl} Win={Config.Trigger.ByWin} " +
                            $"Alt={Config.Trigger.ByAlt} 中键={Config.Trigger.ByMiddleMouse} " +
                            $"自定义=[{CustomTriggerKeys.Describe(Config.Trigger.CustomTriggerKeys)}]");
            }
            catch (Exception ex)
            {
                Logger.Error("读取配置失败，尝试从备份恢复", ex);

                // 先把坏文件留一份取证（此刻 config.json 还是坏内容）
                TryBackupCorruptFile();

                // Save() 每次都会把上一版配置留在 config.backup.json 里，
                // 这里正好拿它兜底 —— 比直接重置回默认配置对用户友好得多。
                if (TryRestoreFromBackup())
                {
                    Logger.Warn("已从 config.backup.json 恢复损坏的配置");
                    return;
                }

                Config = new AppConfig();
                Logger.Warn("没有可用的配置备份，已回退到默认配置");
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
            // 分组对象先兜底：config.json 里显式写了 "Appearance": null 时，
            // 反序列化会得到 null，若直接访问字段会 NRE，被上层 catch 吞掉后
            // 表现为“配置莫名其妙全没了”。
            c.Appearance ??= new AppearanceSettings();
            c.Trigger ??= new TriggerSettings();
            c.System ??= new SystemSettings();

            c.SchemaVersion = 2;

            c.Appearance.ItemCount = c.Appearance.ItemCount == 4 ? 4 : 8;
            c.Appearance.Scale = Clamp(c.Appearance.Scale, 0.6, 1.6, 1.0);
            c.Appearance.ItemSize = Clamp(c.Appearance.ItemSize, 44, 120, 74);
            c.Appearance.DeadZone = Clamp(c.Appearance.DeadZone, 0, 120, 34);
            c.Appearance.Opacity = Clamp(c.Appearance.Opacity, 0.3, 1.0, 1.0);

            c.Trigger.HoldThresholdMs = (int)Clamp(c.Trigger.HoldThresholdMs, 60, 1500, 220);

            // 自定义触发键：名字收敛（去空白/保留键/无法识别的名字、按虚拟键码去重、截断）。
            // 必须在“至少保留一个触发键”的判断之前做，否则一串全是垃圾名字时会误判成
            // “用户还配了自定义键”，结果一个能用的触发键都不剩。
            c.Trigger.CustomTriggerKeys = CustomTriggerKeys.Normalize(c.Trigger.CustomTriggerKeys);

            if (!c.Trigger.ByCtrl && !c.Trigger.ByWin && !c.Trigger.ByAlt &&
                !c.Trigger.ByMiddleMouse && c.Trigger.CustomTriggerKeys.Count == 0)
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
                if (profile == null) continue;
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

        /// <summary>
        /// 读取 config.backup.json 并作为当前配置落盘。
        /// 返回是否恢复成功（文件不存在或内容也坏了就返回 false）。
        /// </summary>
        private bool TryRestoreFromBackup()
        {
            try
            {
                if (!File.Exists(BackupPath)) return false;

                var json = File.ReadAllText(BackupPath);
                var restored = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
                if (restored == null) return false;

                Config = restored;
                Normalize(Config);
                Save();
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn("从 config.backup.json 恢复失败：" + ex.Message);
                return false;
            }
        }

        /// <summary>读取 JSON 根节点里声明的 SchemaVersion；v1 没有这个字段，返回 null。</summary>
        private static int? ReadSchemaVersion(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("SchemaVersion", out var value) &&
                    value.TryGetInt32(out var version))
                {
                    return version;
                }
            }
            catch
            {
                // 解析不了就当没有版本号
            }

            return null;
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
            // 以“已解析结果”为基底，而不是全默认值：alreadyParsed 可能已经带上了
            // JSON 里存在的 v2 字段（WheelItems / Profiles / Environment…），
            // 再用 v1 的扁平字段逐项覆盖。这样即便迁移中途失败，也只是少迁移一点，
            // 绝不会把用户的配色、功能项、应用专属配置整体丢成默认值。
            var result = Clone(alreadyParsed);

            try
            {
                result.Appearance ??= new AppearanceSettings();
                result.Trigger ??= new TriggerSettings();
                result.Environment ??= new EnvironmentSettings();

                var legacy = JsonSerializer.Deserialize<LegacyConfig>(json, JsonOptions);
                if (legacy == null) return result;

                result.Environment.UseWhitelist = legacy.UseWhitelist;
                result.Environment.WhitelistProcesses = legacy.WhitelistProcesses ?? new();
                result.Environment.BlacklistProcesses = legacy.BlacklistProcesses ?? new();

                result.Trigger.ByCtrl = legacy.TriggerByCtrl;
                result.Trigger.ByWin = legacy.TriggerByWin;
                result.Trigger.ByMiddleMouse = legacy.TriggerByMiddleMouse;
                result.Trigger.Enabled = true;

                // v1 没有 Alt 与自定义触发键这两项，迁移后保持出厂默认（关闭 / 空列表），
                // 只在配置里已经是 null 时兜底，避免把用户可能已有的值抹掉。
                result.Trigger.CustomTriggerKeys ??= new();

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
                // 失败也保留已解析的内容，绝不再把用户配置换成一份默认值
                Logger.Error("迁移旧配置失败，保留已解析的配置内容", ex);
                return result;
            }

            return result;
        }

        /// <summary>JSON 往返做一次深拷贝；失败时退回一份默认配置。</summary>
        private static AppConfig Clone(AppConfig config)
        {
            try
            {
                var json = JsonSerializer.Serialize(config, JsonOptions);
                return JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
            }
            catch
            {
                return new AppConfig();
            }
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
