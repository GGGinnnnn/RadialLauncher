using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using RadialLauncher.Models;
using RadialLauncher.Services;

namespace RadialLauncher.Views
{
    /// <summary>后台控制台面板。</summary>
    public partial class SettingsWindow : Window, System.ComponentModel.INotifyPropertyChanged
    {
        private readonly IAppHost _host;
        private readonly ConfigService _configService;
        private readonly ActionExecutor _executor = new();

        /// <summary>编辑用的副本，只有点“保存并关闭”才会写回。</summary>
        private AppConfig _working;

        private readonly ObservableCollection<WindowEntry> _windows = new();
        private readonly ObservableCollection<string> _blacklist = new();
        private readonly ObservableCollection<string> _whitelist = new();
        private readonly ObservableCollection<AppProfile> _profiles = new();
        private readonly ObservableCollection<SlotEntry> _slots = new();
        private readonly List<WheelTarget> _targets = new();

        private readonly DispatcherTimer _statusTimer;

        private readonly string _originalLanguage;
        private int _selectedSlot = -1;

        /// <summary>正在往界面里灌数据，此时控件事件不应该写回模型。</summary>
        private bool _loading;

        /// <summary>
        /// InitializeComponent 完成前，Slider 的 Min/Max 强制转换会触发 ValueChanged，
        /// 那时后面的具名控件还是 null。用这个标志把早期事件全部挡掉。
        /// </summary>
        private bool _controlsReady;

        /// <summary>本面板不需要录制按键（触发键用复选框选择）。</summary>
        public bool IsRecordingKey => false;

        /// <summary>控件事件是否可以安全处理。</summary>
        private bool CanHandleEvents => _controlsReady && !_loading;

        // ==================== 侧边栏 ====================

        /// <summary>展开 / 折叠时的宽度。</summary>
        private const double NavExpandedWidth = 248;
        private const double NavCollapsedWidth = 60;

        private bool _navCollapsed;
        private List<TextBlock> _navLabels = new();

        /// <summary>字体下拉框正在重建时，别把事件当成用户选择。</summary>
        private bool _loadingFonts;

        /// <summary>打开面板时的字体，取消时要还原。</summary>
        private readonly string _originalFont;

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        /// <summary>侧边栏折叠时导航内容居中，展开时靠左（导航项模板里绑定）。</summary>
        public Thickness NavItemContentMargin
            => _navCollapsed ? new Thickness(0) : new Thickness(14, 0, 10, 0);

        public HorizontalAlignment NavItemContentAlignment
            => _navCollapsed ? HorizontalAlignment.Center : HorizontalAlignment.Left;

        private void RaiseNavBindings()
        {
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(NavItemContentMargin)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(NavItemContentAlignment)));
        }

        public SettingsWindow(IAppHost host)
        {
            _host = host;
            _configService = host.ConfigService;
            _originalLanguage = _configService.Config.System.Language;
            _originalFont = _configService.Config.System.UiFont;
            _working = _configService.CreateWorkingCopy();

            InitializeComponent();
            _controlsReady = true;

            _navLabels = new List<TextBlock>
            {
                NavText0, NavText1, NavText2, NavText3, NavText4, NavText5, NavText6,
            };

            LstWindows.ItemsSource = _windows;
            LstBlack.ItemsSource = _blacklist;
            LstWhite.ItemsSource = _whitelist;
            LstSlots.ItemsSource = _slots;
            LstProfiles.ItemsSource = _profiles;

            BuildActionTypeCombo();
            BuildBuiltinCombo();
            BuildLanguageCombo();
            BuildFontCombo();
            LoadAll();

            _statusTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(900),
            };
            _statusTimer.Tick += (_, _) => UpdateStatus();
            _statusTimer.Start();

            Closed += (_, _) => _statusTimer.Stop();
        }

        // ============================================================
        //  载入
        // ============================================================

        private void LoadAll()
        {
            _loading = true;
            try
            {
                var c = _working;

                CbTriggerEnabled.IsChecked = c.Trigger.Enabled;
                CbCtrl.IsChecked = c.Trigger.ByCtrl;
                CbWin.IsChecked = c.Trigger.ByWin;
                CbMiddle.IsChecked = c.Trigger.ByMiddleMouse;
                SldHold.Value = c.Trigger.HoldThresholdMs;
                TxtHoldValue.Text = c.Trigger.HoldThresholdMs + " ms";
                CbSuppressWin.IsChecked = c.Trigger.SuppressWinKey;
                CbSuppressMiddle.IsChecked = c.Trigger.SuppressMiddleClick;
                CbCancelOtherKey.IsChecked = c.Trigger.CancelOnOtherKey;

                RbWhitelist.IsChecked = c.Environment.UseWhitelist;
                RbBlacklist.IsChecked = !c.Environment.UseWhitelist;
                CbSkipFullscreen.IsChecked = c.Environment.SkipFullscreenApps;

                _blacklist.Clear();
                foreach (var p in c.Environment.BlacklistProcesses) _blacklist.Add(p);
                _whitelist.Clear();
                foreach (var p in c.Environment.WhitelistProcesses) _whitelist.Add(p);

                CbProfilesEnabled.IsChecked = c.Environment.ProfilesEnabled;

                _profiles.Clear();
                foreach (var profile in c.Profiles) _profiles.Add(profile);

                RbDir4.IsChecked = c.Appearance.ItemCount == 4;
                RbDir8.IsChecked = c.Appearance.ItemCount == 8;

                TxtBg.Text = c.Appearance.BackgroundColor;
                TxtBorder.Text = c.Appearance.BorderColor;
                TxtItem.Text = c.Appearance.ItemColor;
                TxtSel.Text = c.Appearance.ItemSelectedColor;
                TxtText.Text = c.Appearance.TextColor;

                SldScale.Value = c.Appearance.Scale;
                SldItemSize.Value = c.Appearance.ItemSize;
                SldDeadZone.Value = c.Appearance.DeadZone;
                SldOpacity.Value = c.Appearance.Opacity;

                CbGlow.IsChecked = c.Appearance.ShowGlow;
                CbAnimation.IsChecked = c.Trigger.EnableAnimation;
                CbExecuteOnRelease.IsChecked = c.Appearance.ExecuteOnRelease;

                CbAutoStart.IsChecked = AutoStartService.IsEnabled();
                CbSystemRunAsAdmin.IsChecked = c.System.RunAsAdmin;
                CbShowTrayTip.IsChecked = c.System.ShowTrayTip;
                TxtGitHub.Text = c.System.GitHubUrl;

                SelectLanguageItem(c.System.Language);

                TxtVersion.Text = "Radial Launcher " + GetVersionText();
                TxtHeaderVersion.Text = GetVersionText();
                SelectFontItem(c.System.UiFont);
            }
            finally
            {
                _loading = false;
            }

            UpdateAdminNote();
            UpdateColorSwatches();
            UpdateAppearancePreview();
            RefreshWindowList();
            BuildTargetCombo();
            UpdateStatus();
            RefreshLog();

            // 默认选中第一个配置，否则右侧编辑器是空的，看起来像坏了
            if (_profiles.Count > 0) LstProfiles.SelectedIndex = 0;
            else LoadProfileEditor();
        }

        /// <summary>读取 InformationalVersion（csproj 里设成 0.1），显示为 v0.1。</summary>
        private static string GetVersionText()
        {
            var assembly = typeof(App).Assembly;

            var informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;

            if (!string.IsNullOrWhiteSpace(informational))
            {
                // 去掉 + 后面的构建元数据
                int plus = informational.IndexOf('+');
                if (plus >= 0) informational = informational[..plus];

                informational = informational.Trim();
                return informational.StartsWith('v') || informational.StartsWith('V')
                    ? informational
                    : "v" + informational;
            }

            return "v" + (assembly.GetName().Version?.ToString(3) ?? "0.0.0");
        }

        private void UpdateAdminNote()
        {
            if (ElevationService.IsElevated)
                TxtAdminNote.Text = Loc.T("System_AlreadyAdmin");
            else if (!AutoStartService.IsUserInAdminGroup())
                TxtAdminNote.Text = Loc.T("System_NotAdminUser");
            else
                TxtAdminNote.Text = Loc.T("System_RunAsAdminHint");
        }

        private void UpdateStatus()
        {
            var c = _working;

            var keys = new List<string>();
            if (c.Trigger.ByCtrl) keys.Add("Ctrl");
            if (c.Trigger.ByWin) keys.Add("Win");
            if (c.Trigger.ByMiddleMouse) keys.Add(Loc.Pick("鼠标中键", "Middle mouse"));
            if (keys.Count == 0) keys.Add(Loc.T("Common_None"));

            TxtStatusKeys.Text = (_host.IsRunning ? Loc.T("General_Running") : Loc.T("General_NotRunning"))
                                 + " · " + string.Join(" + ", keys);

            TxtStatusHold.Text = _working.Trigger.HoldThresholdMs + " ms";
            TxtStatusEnv.Text = c.Environment.UseWhitelist
                ? Loc.T("Trigger_Whitelist")
                : Loc.T("Trigger_Blacklist");

            var info = _host.Detector.GetCurrent();
            TxtStatusForeground.Text = string.IsNullOrEmpty(info.ProcessName)
                ? Loc.T("Common_None")
                : info.Display;

            TxtGeneralHint.Text = string.Format(Loc.T("General_Hint"), _working.Trigger.HoldThresholdMs);
        }

        // ============================================================
        //  总览页
        // ============================================================

        private void OnTriggerEnabledChanged(object sender, RoutedEventArgs e)
        {
            if (!CanHandleEvents) return;
            _working.Trigger.Enabled = CbTriggerEnabled.IsChecked == true;
            UpdateStatus();
        }

        private void OnTestWheel(object sender, RoutedEventArgs e)
        {
            // 用界面上的当前设置直接预览，不写回磁盘
            SaveAll();
            _host.TestWheel(_working);
        }

        private void OnOpenDataFolder(object sender, RoutedEventArgs e) => _host.OpenDataFolder();

        private void OnReloadConfig(object sender, RoutedEventArgs e)
        {
            _configService.Load();
            _working = _configService.CreateWorkingCopy();
            LoadAll();
            _host.ApplyConfig();
            ToastWindow.Show(Loc.T("General_Reload"), Loc.Pick("已从磁盘重新加载。", "Reloaded from disk."));
        }

        // ============================================================
        //  触发环境页
        // ============================================================

        private void OnHoldChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!CanHandleEvents) return;

            int value = (int)Math.Round(e.NewValue);
            _working.Trigger.HoldThresholdMs = value;
            TxtHoldValue.Text = value + " ms";
            TxtGeneralHint.Text = string.Format(Loc.T("General_Hint"), value);
            TxtStatusHold.Text = value + " ms";
        }

        private void RefreshWindowList()
        {
            _windows.Clear();
            foreach (var entry in WindowDetectorService.EnumerateWindows())
                _windows.Add(entry);
        }

        private void OnRefreshWindows(object sender, RoutedEventArgs e) => RefreshWindowList();

        private void OnAddBlacklist(object sender, RoutedEventArgs e)
        {
            if (LstWindows.SelectedItem is not WindowEntry entry) return;
            if (!_blacklist.Contains(entry.ProcessName)) _blacklist.Add(entry.ProcessName);
        }

        private void OnAddWhitelist(object sender, RoutedEventArgs e)
        {
            if (LstWindows.SelectedItem is not WindowEntry entry) return;
            if (!_whitelist.Contains(entry.ProcessName)) _whitelist.Add(entry.ProcessName);
        }

        private void OnRemoveBlacklist(object sender, RoutedEventArgs e)
        {
            if (LstBlack.SelectedItem is string value) _blacklist.Remove(value);
        }

        private void OnRemoveWhitelist(object sender, RoutedEventArgs e)
        {
            if (LstWhite.SelectedItem is string value) _whitelist.Remove(value);
        }

        // ============================================================
        //  应用专属页
        // ============================================================

        private void OnProfileSelected(object sender, SelectionChangedEventArgs e)
        {
            if (!_controlsReady) return;
            LoadProfileEditor();
        }

        private void LoadProfileEditor()
        {
            if (!_controlsReady) return;

            if (LstProfiles.SelectedItem is not AppProfile profile)
            {
                ProfileEditor.IsEnabled = false;
                TxtProfileHint.Visibility = Visibility.Visible;
                return;
            }

            ProfileEditor.IsEnabled = true;
            TxtProfileHint.Visibility = Visibility.Collapsed;

            _loading = true;
            try
            {
                TxtProfileName.Text = profile.Name;
                TxtProfileIcon.Text = profile.Icon;
                TxtProfileProcesses.Text = string.Join(Environment.NewLine, profile.ProcessNames);
                TxtProfileTitles.Text = string.Join(Environment.NewLine, profile.TitleKeywords);
                CbProfileEnabled.IsChecked = profile.Enabled;
                CbProfileUseGlobal.IsChecked = profile.UseGlobalItems;

                CmbProfileItemCount.SelectedIndex = profile.ItemCountOverride switch
                {
                    4 => 1,
                    8 => 2,
                    _ => 0,
                };
            }
            finally
            {
                _loading = false;
            }
        }

        private void SaveProfileEditor()
        {
            if (!CanHandleEvents) return;
            if (LstProfiles.SelectedItem is not AppProfile profile) return;

            profile.Name = TxtProfileName.Text;
            profile.Icon = string.IsNullOrWhiteSpace(TxtProfileIcon.Text) ? "🧩" : TxtProfileIcon.Text;
            profile.ProcessNames = SplitLines(TxtProfileProcesses.Text);
            profile.TitleKeywords = SplitLines(TxtProfileTitles.Text);
            profile.Enabled = CbProfileEnabled.IsChecked == true;
            profile.UseGlobalItems = CbProfileUseGlobal.IsChecked == true;

            profile.ItemCountOverride = CmbProfileItemCount.SelectedIndex switch
            {
                1 => 4,
                2 => 8,
                _ => 0,
            };

            LstProfiles.Items.Refresh();
        }

        private static List<string> SplitLines(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return new List<string>();

            return text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                       .Select(s => s.Trim())
                       .Where(s => s.Length > 0)
                       .Distinct(StringComparer.OrdinalIgnoreCase)
                       .ToList();
        }

        private void OnProfileTextChanged(object sender, TextChangedEventArgs e) => SaveProfileEditor();

        private void OnProfileFlagChanged(object sender, RoutedEventArgs e) => SaveProfileEditor();

        private void OnProfileItemCountChanged(object sender, SelectionChangedEventArgs e) => SaveProfileEditor();

        private void OnPickProfileIcon(object sender, RoutedEventArgs e)
        {
            var result = IconPickerWindow.Pick(this, TxtProfileIcon.Text, "");
            if (result == null) return;

            TxtProfileIcon.Text = result.Value.Icon;
        }

        private void OnProfileNew(object sender, RoutedEventArgs e)
        {
            var profile = new AppProfile
            {
                Name = Loc.Pick("新配置", "New profile"),
                Icon = "🧩",
                // 用全局功能项做起点，用户不用从空白开始摆
                Items = _working.WheelItems.ConvertAll(i => i.Clone()),
            };

            _profiles.Add(profile);
            RebuildTargets();
            LstProfiles.SelectedItem = profile;
        }

        private void OnProfileFromCurrent(object sender, RoutedEventArgs e)
        {
            var info = _host.Detector.GetCurrent(force: true);
            if (string.IsNullOrEmpty(info.ProcessName))
            {
                ToastWindow.Show(Loc.T("Profiles_FromCurrent"),
                    Loc.Pick("没有检测到前台窗口。", "No foreground window detected."), error: true);
                return;
            }

            var profile = new AppProfile
            {
                Name = info.ProcessName,
                Icon = "🧩",
                ProcessNames = new List<string> { info.ProcessName },
                Items = _working.WheelItems.ConvertAll(i => i.Clone()),
            };

            _profiles.Add(profile);
            RebuildTargets();
            LstProfiles.SelectedItem = profile;
        }

        private void OnProfileDuplicate(object sender, RoutedEventArgs e)
        {
            if (LstProfiles.SelectedItem is not AppProfile profile) return;

            var copy = profile.Clone();
            copy.Name = profile.Name + " (2)";
            _profiles.Add(copy);
            RebuildTargets();
            LstProfiles.SelectedItem = copy;
        }

        private void OnProfileDelete(object sender, RoutedEventArgs e)
        {
            if (LstProfiles.SelectedItem is not AppProfile profile) return;

            var confirm = MessageBox.Show(
                string.Format(Loc.T("Profiles_DeleteConfirm"), profile.Name),
                Loc.T("Tab_Profiles"),
                MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes) return;

            _profiles.Remove(profile);
            RebuildTargets();
        }

        // ============================================================
        //  外观页
        // ============================================================

        private void OnDirectionChanged(object sender, RoutedEventArgs e)
        {
            if (!CanHandleEvents) return;

            _working.Appearance.ItemCount = RbDir8.IsChecked == true ? 8 : 4;

            UpdateAppearancePreview();

            // 全局轮盘的槽位数量跟着变
            if (CmbTarget.SelectedItem is WheelTarget { Profile: null })
                RefreshSlots();
        }

        private void OnColorTextChanged(object sender, TextChangedEventArgs e)
        {
            if (!CanHandleEvents) return;

            ApplyColorsToWorking();
            UpdateColorSwatches();
            UpdateAppearancePreview();
        }

        private void ApplyColorsToWorking()
        {
            var a = _working.Appearance;
            a.BackgroundColor = TxtBg.Text;
            a.BorderColor = TxtBorder.Text;
            a.ItemColor = TxtItem.Text;
            a.ItemSelectedColor = TxtSel.Text;
            a.TextColor = TxtText.Text;
        }

        private void UpdateColorSwatches()
        {
            SwatchBg.Background = SwatchBrush(TxtBg.Text, "#E61A1A2E");
            SwatchBorder.Background = SwatchBrush(TxtBorder.Text, "#FF4A9EFF");
            SwatchItem.Background = SwatchBrush(TxtItem.Text, "#E62A2A3A");
            SwatchSel.Background = SwatchBrush(TxtSel.Text, "#FFFF6B35");
            SwatchText.Background = SwatchBrush(TxtText.Text, "#FFFFFFFF");
        }

        private static Brush SwatchBrush(string text, string fallback)
            => new SolidColorBrush(BrushHelper.ParseColor(text, BrushHelper.ParseColor(fallback, Colors.Gray)));

        private void OnSwatchClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement { Tag: string targetName }) OpenColorPicker(targetName);
        }

        private void OnPickColor(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: string targetName }) OpenColorPicker(targetName);
        }

        private void OpenColorPicker(string textBoxName)
        {
            if (FindName(textBoxName) is not TextBox box) return;

            var current = BrushHelper.ParseColor(box.Text, Colors.White);
            var picked = ColorPickerWindow.Pick(this, current);
            if (picked == null) return;

            box.Text = BrushHelper.ToHex(picked.Value);
        }

        private void OnResetColors(object sender, RoutedEventArgs e)
        {
            var defaults = new AppearanceSettings();

            TxtBg.Text = defaults.BackgroundColor;
            TxtBorder.Text = defaults.BorderColor;
            TxtItem.Text = defaults.ItemColor;
            TxtSel.Text = defaults.ItemSelectedColor;
            TxtText.Text = defaults.TextColor;
        }

        private void OnAppearanceSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!CanHandleEvents) return;

            var a = _working.Appearance;
            a.Scale = SldScale.Value;
            a.ItemSize = SldItemSize.Value;
            a.DeadZone = SldDeadZone.Value;
            a.Opacity = SldOpacity.Value;

            TxtScaleValue.Text = a.Scale.ToString("0.00");
            TxtItemSizeValue.Text = a.ItemSize.ToString("0");
            TxtDeadZoneValue.Text = a.DeadZone.ToString("0");
            TxtOpacityValue.Text = a.Opacity.ToString("0.00");

            UpdateAppearancePreview();
        }

        private void OnAppearanceFlagChanged(object sender, RoutedEventArgs e)
        {
            if (!CanHandleEvents) return;

            _working.Appearance.ShowGlow = CbGlow.IsChecked == true;
            _working.Trigger.EnableAnimation = CbAnimation.IsChecked == true;
            _working.Appearance.ExecuteOnRelease = CbExecuteOnRelease.IsChecked == true;

            UpdateAppearancePreview();
        }

        private void UpdateAppearancePreview()
        {
            var items = CmbTarget.SelectedItem is WheelTarget target
                ? target.Items
                : _working.WheelItems;

            Preview.Render(_working.Appearance, items, -1);

            TxtScaleValue.Text = _working.Appearance.Scale.ToString("0.00");
            TxtItemSizeValue.Text = _working.Appearance.ItemSize.ToString("0");
            TxtDeadZoneValue.Text = _working.Appearance.DeadZone.ToString("0");
            TxtOpacityValue.Text = _working.Appearance.Opacity.ToString("0.00");
        }

        // ============================================================
        //  功能选择页
        // ============================================================

        private static readonly string[] ActionTypes =
        {
            "LaunchApp", "OpenFolder", "OpenUrl", "Shortcut", "Command", "Script", "Builtin",
        };

        private sealed class WheelTarget
        {
            public string Name { get; init; } = "";
            public AppProfile? Profile { get; init; }
            public List<WheelItem> Items => Profile?.Items ?? _globalItems!;

            public List<WheelItem>? _globalItems;

            public override string ToString() => Name;
        }

        private sealed class SlotEntry
        {
            public int Index { get; init; }
            public WheelItem Item { get; init; } = new();

            public override string ToString()
            {
                var icon = string.IsNullOrWhiteSpace(Item.Icon) ? "●" : Item.Icon;
                var name = string.IsNullOrWhiteSpace(Item.Name) ? Loc.T("Functions_EmptySlot") : Item.Name;
                return $"{Index + 1}.   {icon}   {name}";
            }
        }

        private void BuildActionTypeCombo()
        {
            var current = SelectedActionType();

            _loading = true;
            try
            {
                CmbType.Items.Clear();
                foreach (var type in ActionTypes)
                {
                    CmbType.Items.Add(new ComboBoxItem
                    {
                        Content = Loc.T("Action_" + type),
                        Tag = type,
                    });
                }
            }
            finally
            {
                _loading = false;
            }

            SelectActionType(current);
        }

        private void BuildBuiltinCombo()
        {
            var current = CmbBuiltin.SelectedItem is ComboBoxItem { Tag: string id } ? id : "";

            bool english = Loc.Instance.IsEnglish;
            CmbBuiltin.Items.Clear();

            foreach (var category in BuiltinCatalog.Categories())
            {
                foreach (var def in BuiltinCatalog.Items)
                {
                    var defCategory = english ? def.CategoryEn : def.CategoryZh;
                    if (defCategory != category) continue;

                    CmbBuiltin.Items.Add(new ComboBoxItem
                    {
                        Content = $"{def.Icon}   {(english ? def.NameEn : def.NameZh)}   ·   {category}",
                        Tag = def.Id,
                    });
                }
            }

            foreach (ComboBoxItem item in CmbBuiltin.Items)
            {
                if ((string)item.Tag == current) { CmbBuiltin.SelectedItem = item; break; }
            }
        }

        private void BuildLanguageCombo()
        {
            SelectLanguageItem(_working.System.Language);
        }

        private void SelectLanguageItem(string language)
        {
            foreach (ComboBoxItem item in CmbLanguage.Items)
            {
                if (string.Equals((string)item.Tag, language, StringComparison.OrdinalIgnoreCase))
                {
                    CmbLanguage.SelectedItem = item;
                    return;
                }
            }

            CmbLanguage.SelectedIndex = 0;
        }

        private string SelectedActionType()
        {
            return CmbType.SelectedItem is ComboBoxItem { Tag: string type } ? type : "Shortcut";
        }

        // ==================== 界面字体 ====================

        private void BuildFontCombo()
        {
            _loadingFonts = true;
            try
            {
                var recommendations = FontCatalog.Recommendations();
                var recommendedNames = new HashSet<string>(
                    recommendations.Select(r => r.Family), StringComparer.OrdinalIgnoreCase);

                CmbUiFont.Items.Clear();

                // 先放推荐字体（含未安装的，标出来并禁用）
                foreach (var option in recommendations)
                {
                    var note = Loc.Instance.IsEnglish ? option.NoteEn : option.NoteZh;

                    CmbUiFont.Items.Add(new ComboBoxItem
                    {
                        Content = option.Display(Loc.Instance.IsEnglish),
                        Tag = option.Family,
                        IsEnabled = option.Installed,
                        ToolTip = string.IsNullOrWhiteSpace(note) ? null : note,
                    });
                }

                // 再放本机其它已安装字体
                foreach (var family in FontCatalog.InstalledFamilies)
                {
                    if (recommendedNames.Contains(family)) continue;
                    CmbUiFont.Items.Add(new ComboBoxItem { Content = family, Tag = family });
                }

                SelectFontItem(_working.System.UiFont);
            }
            finally
            {
                _loadingFonts = false;
            }
        }

        private void SelectFontItem(string family)
        {
            foreach (var item in CmbUiFont.Items)
            {
                if (item is ComboBoxItem { Tag: string tag } &&
                    string.Equals(tag, family, StringComparison.OrdinalIgnoreCase))
                {
                    CmbUiFont.SelectedItem = item;
                    ApplyFontPreview(family);
                    return;
                }
            }

            CmbUiFont.SelectedIndex = -1;
        }

        private void ApplyFontPreview(string family)
        {
            TxtFontPreview.FontFamily = FontCatalog.Resolve(family);

            var effective = FontCatalog.DescribeEffective(family);

            TxtFontNote.Text = string.Equals(effective, family, StringComparison.OrdinalIgnoreCase)
                ? string.Format(Loc.T("System_FontEffective"), effective)
                : string.Format(Loc.T("System_FontFallback"), family, effective);
        }

        private void OnUiFontChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingFonts || !_controlsReady) return;
            if (CmbUiFont.SelectedItem is not ComboBoxItem { Tag: string family }) return;

            _working.System.UiFont = family;
            ApplyFontPreview(family);

            // 立刻全局生效，用户能马上看到效果；取消时再还原
            ThemeManager.ApplyUiFont(family);
        }

        private void OnReloadFonts(object sender, RoutedEventArgs e)
        {
            FontCatalog.InvalidateCache();
            BuildFontCombo();
            ToastWindow.Show(Loc.T("System_FontReload"),
                Loc.Pick("已重新扫描系统字体。", "System fonts rescanned."));
        }

        // ==================== 侧边栏 ====================

        private void OnNavChanged(object sender, RoutedEventArgs e)
        {
            if (!_controlsReady) return;   // InitializeComponent 期间 NavGeneral 会先触发一次
            if (sender is not RadioButton { Tag: string key }) return;

            ShowPage(key);
        }

        private void ShowPage(string key)
        {
            PageGeneral.Visibility = key == "General" ? Visibility.Visible : Visibility.Collapsed;
            PageTrigger.Visibility = key == "Trigger" ? Visibility.Visible : Visibility.Collapsed;
            PageProfiles.Visibility = key == "Profiles" ? Visibility.Visible : Visibility.Collapsed;
            PageAppearance.Visibility = key == "Appearance" ? Visibility.Visible : Visibility.Collapsed;
            PageFunctions.Visibility = key == "Functions" ? Visibility.Visible : Visibility.Collapsed;
            PageSystem.Visibility = key == "System" ? Visibility.Visible : Visibility.Collapsed;
            PageAbout.Visibility = key == "About" ? Visibility.Visible : Visibility.Collapsed;

            // 切到这两页时刷新一下，免得看到的是旧内容
            if (key == "Appearance") UpdateAppearancePreview();
            else if (key == "About") RefreshLog();
            else if (key == "Functions") UpdateAppearancePreview();
        }

        /// <summary>折叠 / 展开侧边栏：宽度与文字同时做动画。</summary>
        private void OnToggleNav(object sender, RoutedEventArgs e)
        {
            _navCollapsed = !_navCollapsed;

            // 折叠时文字要先显示出来做淡出；展开时同理先显示再做淡入
            foreach (var label in _navLabels) label.Visibility = Visibility.Visible;
            RaiseNavBindings();

            double target = _navCollapsed ? NavCollapsedWidth : NavExpandedWidth;
            double from = NavPanel.ActualWidth > 0
                ? NavPanel.ActualWidth
                : (_navCollapsed ? NavExpandedWidth : NavCollapsedWidth);

            var widthAnimation = new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(240))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };

            widthAnimation.Completed += (_, _) =>
            {
                NavPanel.Width = target;
                NavPanel.BeginAnimation(WidthProperty, null);

                if (_navCollapsed)
                {
                    foreach (var label in _navLabels) label.Visibility = Visibility.Collapsed;
                }
            };

            NavPanel.BeginAnimation(WidthProperty, widthAnimation);

            double opacityTo = _navCollapsed ? 0 : 1;
            foreach (var label in _navLabels)
            {
                label.BeginAnimation(OpacityProperty,
                    new DoubleAnimation(opacityTo, TimeSpan.FromMilliseconds(180))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                    });
            }
        }

        private void SelectActionType(string type)
        {
            bool previous = _loading;
            _loading = true;
            try
            {
                foreach (ComboBoxItem item in CmbType.Items)
                {
                    if ((string)item.Tag == type)
                    {
                        CmbType.SelectedItem = item;
                        return;
                    }
                }
                if (CmbType.Items.Count > 0) CmbType.SelectedIndex = 0;
            }
            finally
            {
                _loading = previous;
            }
        }

        private WheelTarget? CurrentTarget => CmbTarget.SelectedItem as WheelTarget;

        private void BuildTargetCombo()
        {
            var previous = CmbTarget.SelectedIndex;

            _loading = true;
            try
            {
                _targets.Clear();
                _targets.Add(new WheelTarget
                {
                    Name = "🌐  " + Loc.Pick("全局轮盘", "Global wheel"),
                    Profile = null,
                    _globalItems = _working.WheelItems,
                });

                int index = 1;
                foreach (var profile in _profiles)
                {
                    var label = string.IsNullOrWhiteSpace(profile.Name)
                        ? Loc.Pick("配置 ", "Profile ") + index
                        : profile.Name;

                    _targets.Add(new WheelTarget
                    {
                        Name = $"{profile.Icon}  {label}",
                        Profile = profile,
                    });
                    index++;
                }

                CmbTarget.ItemsSource = null;
                CmbTarget.ItemsSource = _targets;
                CmbTarget.SelectedIndex = previous >= 0 && previous < _targets.Count ? previous : 0;
            }
            finally
            {
                _loading = false;
            }

            RefreshSlots();
        }

        /// <summary>配置列表变化后同步下拉框，同时保持当前选中项。</summary>
        private void RebuildTargets() => BuildTargetCombo();

        private int GetSlotCount(WheelTarget target)
        {
            if (target.Profile == null) return _working.Appearance.ItemCount;

            return target.Profile.ItemCountOverride is 4 or 8
                ? target.Profile.ItemCountOverride
                : _working.Appearance.ItemCount;
        }

        private void OnTargetChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!CanHandleEvents) return;
            RefreshSlots();
        }

        private void RefreshSlots()
        {
            var target = CurrentTarget;

            _slots.Clear();
            _selectedSlot = -1;

            if (target == null)
            {
                LstSlots.IsEnabled = false;
                SlotEditor.IsEnabled = false;
                return;
            }

            bool readOnly = target.Profile is { UseGlobalItems: true };
            LstSlots.IsEnabled = !readOnly;
            SlotEditor.IsEnabled = !readOnly;

            TxtTargetNote.Text = readOnly
                ? Loc.T("Profiles_UseGlobal")
                : "";

            int count = GetSlotCount(target);
            var items = target.Items;

            // 补齐短于方向数的槽位；多出来的保留着，方便用户切回八向时找回
            while (items.Count < count)
            {
                items.Add(new WheelItem { Name = "", Icon = "●", ActionType = "Shortcut", ActionValue = "" });
            }

            for (int i = 0; i < count; i++) _slots.Add(new SlotEntry { Index = i, Item = items[i] });

            LstSlots.SelectedIndex = count > 0 ? 0 : -1;

            UpdateAppearancePreview();
        }

        private void OnSlotSelected(object sender, SelectionChangedEventArgs e)
        {
            if (!_controlsReady) return;
            LoadSlotEditor();
        }

        private void LoadSlotEditor()
        {
            if (!_controlsReady) return;

            if (LstSlots.SelectedItem is not SlotEntry slot)
            {
                _selectedSlot = -1;
                SlotEditor.IsEnabled = false;
                return;
            }

            SlotEditor.IsEnabled = true;
            _selectedSlot = slot.Index;
            var item = slot.Item;

            _loading = true;
            try
            {
                TxtName.Text = item.Name;
                TxtIcon.Text = item.Icon;
                SelectActionType(item.ActionType);
                TxtValue.Text = item.ActionValue;
                TxtArgs.Text = item.Arguments;
                TxtWorkDir.Text = item.WorkingDirectory;
                CbRunAsAdmin.IsChecked = item.RunAsAdmin;
                CbHidden.IsChecked = item.HiddenWindow;
                SelectBuiltin(item.ActionValue);
            }
            finally
            {
                _loading = false;
            }

            UpdateActionTypeUi();
        }

        private void SelectBuiltin(string id)
        {
            foreach (ComboBoxItem item in CmbBuiltin.Items)
            {
                if (string.Equals((string)item.Tag, id, StringComparison.OrdinalIgnoreCase))
                {
                    CmbBuiltin.SelectedItem = item;
                    return;
                }
            }
            CmbBuiltin.SelectedIndex = -1;
        }

        private void SaveSlotEditor()
        {
            if (!CanHandleEvents || _selectedSlot < 0) return;
            if (LstSlots.SelectedItem is not SlotEntry slot) return;

            var item = slot.Item;
            item.Name = TxtName.Text;
            item.Icon = string.IsNullOrWhiteSpace(TxtIcon.Text) ? "●" : TxtIcon.Text;
            item.ActionType = SelectedActionType();
            item.Arguments = TxtArgs.Text;
            item.WorkingDirectory = TxtWorkDir.Text;
            item.RunAsAdmin = CbRunAsAdmin.IsChecked == true;
            item.HiddenWindow = CbHidden.IsChecked == true;

            // Builtin 的目标来自下拉框，其它类型来自文本框
            if (item.ActionType == "Builtin")
                item.ActionValue = CmbBuiltin.SelectedItem is ComboBoxItem { Tag: string id } ? id : "";
            else
                item.ActionValue = TxtValue.Text;

            LstSlots.Items.Refresh();
        }

        private void OnSlotTextChanged(object sender, TextChangedEventArgs e) => SaveSlotEditor();

        private void OnSlotFlagChanged(object sender, RoutedEventArgs e) => SaveSlotEditor();

        private void OnBuiltinChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!CanHandleEvents) return;
            SaveSlotEditor();
        }

        private void OnActionTypeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!CanHandleEvents) return;
            SaveSlotEditor();
            UpdateActionTypeUi();
        }

        private void UpdateActionTypeUi()
        {
            var type = SelectedActionType();

            bool isBuiltin = type == "Builtin";
            bool isShortcut = type == "Shortcut";

            CmbBuiltin.Visibility = isBuiltin ? Visibility.Visible : Visibility.Collapsed;
            TxtValue.Visibility = isBuiltin ? Visibility.Collapsed : Visibility.Visible;
            BtnBrowse.Visibility = isBuiltin || type == "OpenUrl" ? Visibility.Collapsed : Visibility.Visible;
            BtnShortcut.Visibility = isShortcut ? Visibility.Visible : Visibility.Collapsed;

            TxtArgs.IsEnabled = type is "LaunchApp" or "Command" or "Script";
            TxtWorkDir.IsEnabled = type is "LaunchApp" or "Command" or "Script";
            CbHidden.IsEnabled = type is "Command" or "Script";

            LblValue.Text = Loc.T("Functions_Value");

            var iconPath = CurrentSlotItem()?.IconPath ?? "";
            TxtIconMode.Text = iconPath == "auto"
                ? Loc.Pick("自动提取图标", "Auto icon")
                : string.IsNullOrWhiteSpace(iconPath) ? "" : iconPath;

            // 快捷键类型显示匹配到的功能名
            if (isShortcut)
            {
                var match = ShortcutCatalog.ByKeys(TxtValue.Text);
                TxtValueNote.Text = match == null
                    ? (string.IsNullOrWhiteSpace(TxtValue.Text) ? "" : Loc.T("Functions_ShortcutCustom"))
                    : string.Format(Loc.T("Functions_ShortcutMatched"),
                        ShortcutCatalog.DisplayName(match, Loc.Instance.IsEnglish));
            }
            else if (type == "OpenFolder")
            {
                TxtValueNote.Text = Loc.Pick("留空则打开“此电脑”", "Leave empty to open This PC");
            }
            else if (type == "Command")
            {
                TxtValueNote.Text = Loc.Pick("例如：npm run dev", "For example: npm run dev");
            }
            else if (type == "Script")
            {
                TxtValueNote.Text = Loc.Pick("支持 .ps1 / .bat / .cmd / .py", "Supports .ps1 / .bat / .cmd / .py");
            }
            else
            {
                TxtValueNote.Text = "";
            }
        }

        private WheelItem? CurrentSlotItem()
            => LstSlots.SelectedItem is SlotEntry slot ? slot.Item : null;

        private void OnPickIcon(object sender, RoutedEventArgs e)
        {
            var current = CurrentSlotItem();
            if (current == null) return;

            var result = IconPickerWindow.Pick(this, current.Icon, current.IconPath);
            if (result == null) return;

            current.IconPath = result.Value.IconPath;
            current.Icon = result.Value.Icon;

            _loading = true;
            try
            {
                TxtIcon.Text = current.Icon;
            }
            finally
            {
                _loading = false;
            }

            UpdateActionTypeUi();
            LstSlots.Items.Refresh();
        }

        private void OnPickShortcut(object sender, RoutedEventArgs e)
        {
            if (CurrentSlotItem() == null) return;

            var result = ShortcutPickerWindow.Pick(this, TxtValue.Text);
            if (result == null) return;

            TxtValue.Text = result.Value.Keys;
            TxtName.Text = string.IsNullOrWhiteSpace(TxtName.Text) ? result.Value.Name : TxtName.Text;

            SaveSlotEditor();
            UpdateActionTypeUi();
        }

        private void OnBrowseValue(object sender, RoutedEventArgs e)
        {
            var type = SelectedActionType();

            if (type == "OpenFolder")
            {
                var folder = PickFolder(Loc.T("Functions_Value"));
                if (folder != null) TxtValue.Text = folder;
                return;
            }

            var filter = type switch
            {
                "LaunchApp" => Loc.Pick("程序 (*.exe)|*.exe|所有文件 (*.*)|*.*",
                                        "Programs (*.exe)|*.exe|All files (*.*)|*.*"),
                "Script" => Loc.Pick("脚本 (*.ps1;*.bat;*.cmd;*.py)|*.ps1;*.bat;*.cmd;*.py|所有文件 (*.*)|*.*",
                                     "Scripts (*.ps1;*.bat;*.cmd;*.py)|*.ps1;*.bat;*.cmd;*.py|All files (*.*)|*.*"),
                _ => Loc.Pick("所有文件 (*.*)|*.*", "All files (*.*)|*.*"),
            };

            var dialog = new OpenFileDialog
            {
                Title = Loc.T("Functions_Value"),
                Filter = filter,
                CheckFileExists = true,
            };

            if (dialog.ShowDialog(this) != true) return;

            TxtValue.Text = dialog.FileName;

            if (type == "LaunchApp" && string.IsNullOrWhiteSpace(TxtName.Text))
                TxtName.Text = System.IO.Path.GetFileNameWithoutExtension(dialog.FileName);
        }

        private void OnBrowseWorkDir(object sender, RoutedEventArgs e)
        {
            var folder = PickFolder(Loc.T("Functions_WorkingDir"));
            if (folder != null) TxtWorkDir.Text = folder;
        }

        private string? PickFolder(string title)
        {
            try
            {
                var dialog = new OpenFolderDialog
                {
                    Title = title,
                    Multiselect = false,
                };

                if (!string.IsNullOrWhiteSpace(TxtWorkDir.Text))
                    dialog.InitialDirectory = TxtWorkDir.Text;

                return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
            }
            catch (Exception ex)
            {
                Logger.Error("选择文件夹失败", ex);
                return null;
            }
        }

        private void OnTestSlot(object sender, RoutedEventArgs e)
        {
            var item = CurrentSlotItem();
            if (item == null)
            {
                ToastWindow.Show(Loc.T("Functions_TestRun"), Loc.T("Functions_SelectSlotFirst"), error: true);
                return;
            }

            SaveSlotEditor();

            if (string.IsNullOrWhiteSpace(item.ActionValue) && item.ActionType != "OpenFolder")
            {
                ToastWindow.Show(Loc.T("Functions_TestRun"), Loc.T("Msg_NotConfigured"), error: true);
                return;
            }

            var result = _executor.Execute(item);

            ToastWindow.Show(
                result.Success ? Loc.T("Functions_TestRun") : Loc.T("Msg_ActionFailed"),
                result.Success ? item.Name : result.Message + "  " + result.Detail,
                error: !result.Success,
                seconds: result.Success ? 2.0 : 5.0);
        }

        private void OnClearSlot(object sender, RoutedEventArgs e)
        {
            var item = CurrentSlotItem();
            if (item == null) return;

            item.Name = "";
            item.Icon = "●";
            item.IconPath = "";
            item.ActionType = "Shortcut";
            item.ActionValue = "";
            item.Arguments = "";
            item.WorkingDirectory = "";
            item.RunAsAdmin = false;
            item.HiddenWindow = false;

            LoadSlotEditor();
        }

        // ============================================================
        //  系统设置页
        // ============================================================

        private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbLanguage.SelectedItem is not ComboBoxItem { Tag: string language }) return;

            _working.System.Language = language;
            Loc.Instance.SetLanguage(language);

            if (!CanHandleEvents) return;

            // 语言变了，动态生成的列表要重建
            BuildActionTypeCombo();
            BuildBuiltinCombo();
            BuildTargetCombo();
            UpdateActionTypeUi();
            UpdateAdminNote();
            UpdateStatus();
        }

        private void OnOpenGitHub(object sender, RoutedEventArgs e)
        {
            var url = TxtGitHub.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(url)) return;

            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) url = "https://" + url;

            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Logger.Error("打开 GitHub 失败", ex);
            }
        }

        private void OnFactoryReset(object sender, RoutedEventArgs e)
        {
            var confirm = MessageBox.Show(Loc.T("System_ResetConfirm"), Loc.T("System_Reset"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes) return;

            _configService.ResetToDefaults();
            _working = _configService.CreateWorkingCopy();

            BuildActionTypeCombo();
            BuildBuiltinCombo();
            LoadAll();

            _host.ApplyConfig();

            ToastWindow.Show(Loc.T("System_Reset"), Loc.T("System_ResetDone"));
        }

        // ============================================================
        //  关于页
        // ============================================================

        private void RefreshLog() => TxtLog.Text = Logger.ReadTail(500);

        private void OnRefreshLog(object sender, RoutedEventArgs e) => RefreshLog();

        private void OnClearLog(object sender, RoutedEventArgs e)
        {
            Logger.Clear();
            RefreshLog();
        }

        private void OnOpenLogFile(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!System.IO.File.Exists(Logger.LogPath)) Logger.Info("（用户打开了日志）");
                Process.Start(new ProcessStartInfo(Logger.LogPath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Logger.Error("打开日志失败", ex);
            }
        }

        // ============================================================
        //  保存 / 取消
        // ============================================================

        private void OnSaveAndClose(object sender, RoutedEventArgs e)
        {
            SaveAll();
            _configService.Replace(_working);
            _host.ApplyConfig();
            Close();
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            // 语言和字体都是即时生效的，取消时要还原
            Loc.Instance.SetLanguage(_originalLanguage);
            ThemeManager.ApplyUiFont(_originalFont);
            _host.ApplyConfig();
            Close();
        }

        private void SaveAll()
        {
            var c = _working;

            c.Trigger.Enabled = CbTriggerEnabled.IsChecked == true;
            c.Trigger.ByCtrl = CbCtrl.IsChecked == true;
            c.Trigger.ByWin = CbWin.IsChecked == true;
            c.Trigger.ByMiddleMouse = CbMiddle.IsChecked == true;
            c.Trigger.HoldThresholdMs = (int)Math.Round(SldHold.Value);
            c.Trigger.SuppressWinKey = CbSuppressWin.IsChecked == true;
            c.Trigger.SuppressMiddleClick = CbSuppressMiddle.IsChecked == true;
            c.Trigger.CancelOnOtherKey = CbCancelOtherKey.IsChecked == true;
            c.Trigger.EnableAnimation = CbAnimation.IsChecked == true;

            c.Environment.UseWhitelist = RbWhitelist.IsChecked == true;
            c.Environment.SkipFullscreenApps = CbSkipFullscreen.IsChecked == true;
            c.Environment.ProfilesEnabled = CbProfilesEnabled.IsChecked == true;
            c.Environment.BlacklistProcesses = _blacklist.ToList();
            c.Environment.WhitelistProcesses = _whitelist.ToList();

            c.Profiles = _profiles.ToList();

            c.Appearance.ItemCount = RbDir8.IsChecked == true ? 8 : 4;
            ApplyColorsToWorking();
            c.Appearance.Scale = SldScale.Value;
            c.Appearance.ItemSize = SldItemSize.Value;
            c.Appearance.DeadZone = SldDeadZone.Value;
            c.Appearance.Opacity = SldOpacity.Value;
            c.Appearance.ShowGlow = CbGlow.IsChecked == true;
            c.Appearance.ExecuteOnRelease = CbExecuteOnRelease.IsChecked == true;

            if (CmbLanguage.SelectedItem is ComboBoxItem { Tag: string language })
                c.System.Language = language;
            c.System.AutoStart = CbAutoStart.IsChecked == true;
            c.System.RunAsAdmin = CbSystemRunAsAdmin.IsChecked == true;
            c.System.ShowTrayTip = CbShowTrayTip.IsChecked == true;
            c.System.GitHubUrl = TxtGitHub.Text?.Trim() ?? "";

            if (CmbUiFont.SelectedItem is ComboBoxItem { Tag: string font })
                c.System.UiFont = font;

            AutoStartService.SetEnabled(c.System.AutoStart);

            ConfigService.Normalize(c);
        }
    }
}
