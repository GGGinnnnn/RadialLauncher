using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RadialLauncher.Services;

namespace RadialLauncher.Views
{
    /// <summary>从内置的 Windows 快捷键目录里挑一条。</summary>
    public partial class ShortcutPickerWindow : Window
    {
        /// <summary>列表行。</summary>
        public sealed class Row
        {
            public string Id { get; init; } = "";
            public string Category { get; init; } = "";
            public string Name { get; init; } = "";
            public string Keys { get; init; } = "";
        }

        private List<Row> _all = new();
        private bool _suppressCategoryEvent;

        /// <summary>选中的按键序列，例如 "Ctrl+Shift+Esc"。</summary>
        public string SelectedKeys { get; private set; } = "";
        public string SelectedName { get; private set; } = "";

        public ShortcutPickerWindow(string? currentKeys = null)
        {
            InitializeComponent();

            SearchBox.ToolTip = Loc.T("Shortcut_Search");
            BuildRows();
            BuildCategories();

            // 尝试定位到当前快捷键
            if (!string.IsNullOrWhiteSpace(currentKeys))
            {
                var match = ShortcutCatalog.ByKeys(currentKeys);
                if (match != null)
                {
                    CategoryBox.SelectedItem = Loc.T("Shortcut_AllCategories");
                    SelectRowById(match.Id);
                }
            }
        }

        /// <summary>弹出选择器；取消返回 null。</summary>
        public static (string Keys, string Name)? Pick(Window? owner, string? currentKeys)
        {
            var window = new ShortcutPickerWindow(currentKeys);
            if (owner != null && owner.IsVisible) window.Owner = owner;

            if (window.ShowDialog() != true) return null;
            return (window.SelectedKeys, window.SelectedName);
        }

        private void BuildRows()
        {
            bool english = Loc.Instance.IsEnglish;

            _all = new List<Row>(ShortcutCatalog.Items.Count);
            foreach (var item in ShortcutCatalog.Items)
            {
                _all.Add(new Row
                {
                    Id = item.Id,
                    Category = ShortcutCatalog.CategoryName(item, english),
                    Name = ShortcutCatalog.DisplayName(item, english),
                    Keys = item.Keys,
                });
            }
        }

        private void BuildCategories()
        {
            _suppressCategoryEvent = true;

            CategoryBox.Items.Clear();
            CategoryBox.Items.Add(Loc.T("Shortcut_AllCategories"));

            bool english = Loc.Instance.IsEnglish;
            foreach (var category in ShortcutCatalog.Categories(english))
                CategoryBox.Items.Add(category);

            CategoryBox.SelectedIndex = 0;
            _suppressCategoryEvent = false;

            ApplyFilter();
        }

        private void OnSearchChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

        private void OnCategoryChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressCategoryEvent) return;
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            var query = SearchBox.Text?.Trim() ?? "";
            var category = CategoryBox.SelectedItem as string;
            bool allCategories = string.IsNullOrEmpty(category) || category == Loc.T("Shortcut_AllCategories");

            var filtered = new List<Row>();

            foreach (var row in _all)
            {
                if (!allCategories && !string.Equals(row.Category, category, StringComparison.Ordinal))
                    continue;

                if (query.Length > 0 &&
                    row.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0 &&
                    row.Keys.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0 &&
                    row.Category.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                filtered.Add(row);
            }

            Grid.ItemsSource = filtered;
            if (filtered.Count > 0) Grid.SelectedIndex = 0;
        }

        private void SelectRowById(string id)
        {
            if (Grid.ItemsSource is not List<Row> rows) return;

            for (int i = 0; i < rows.Count; i++)
            {
                if (string.Equals(rows[i].Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    Grid.SelectedIndex = i;
                    Grid.ScrollIntoView(rows[i]);
                    return;
                }
            }
        }

        private void OnRowDoubleClick(object sender, MouseButtonEventArgs e) => Confirm();

        private void OnSelect(object sender, RoutedEventArgs e) => Confirm();

        private void Confirm()
        {
            if (Grid.SelectedItem is not Row row) return;

            SelectedKeys = row.Keys;
            SelectedName = row.Name;
            DialogResult = true;
            Close();
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
