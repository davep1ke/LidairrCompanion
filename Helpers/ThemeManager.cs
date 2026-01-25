using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using LidarrCompanion.Models;

namespace LidarrCompanion.Helpers
{
    /// <summary>
    /// Manages application-wide theming including dark/light mode, color converters, and control styling
    /// </summary>
    public static class ThemeManager
    {
        #region Theme Application

        // Apply theme (dark or light) to the provided window and its visual children
        public static void ApplyTheme(Window window)
        {
            if (window == null) return;
            var darkModeEnabled = AppSettings.Current.GetTyped<bool>(SettingKey.DarkMode);

            // Choose palette
            var background = darkModeEnabled
                ? new SolidColorBrush(Color.FromRgb(30, 30, 30))
                : Brushes.White;

            var controlBackground = darkModeEnabled
                ? new SolidColorBrush(Color.FromRgb(45, 45, 45))
                : Brushes.White;

            var borderBrush = darkModeEnabled
                ? new SolidColorBrush(Color.FromRgb(60, 60, 60))
                : new SolidColorBrush(Color.FromRgb(171, 173, 179));

            var textBrush = darkModeEnabled
                ? new SolidColorBrush(Color.FromRgb(220, 220, 220))
                : Brushes.Black;

            var buttonBackground = darkModeEnabled
                ? new SolidColorBrush(Color.FromRgb(55, 55, 55))
                : new SolidColorBrush(Color.FromRgb(221, 221, 221));

            var buttonBorder = darkModeEnabled
                ? new SolidColorBrush(Color.FromRgb(80, 80, 80))
                : new SolidColorBrush(Color.FromRgb(112, 112, 112));

            var buttonDisabledBackground = darkModeEnabled
                ? new SolidColorBrush(Color.FromRgb(40, 40, 40))
                : new SolidColorBrush(Color.FromRgb(240, 240, 240));

            var disabledTextBrush = darkModeEnabled
                ? new SolidColorBrush(Color.FromRgb(100, 100, 100))
                : new SolidColorBrush(Color.FromRgb(160, 160, 160));

            var headerBackground = darkModeEnabled
                ? new SolidColorBrush(Color.FromRgb(35, 35, 35))
                : new SolidColorBrush(Color.FromRgb(245, 245, 245));

            var selectionBackground = darkModeEnabled
                ? new SolidColorBrush(Color.FromRgb(70, 70, 70))
                : SystemColors.HighlightBrush;

            var selectionTextBrush = darkModeEnabled
                ? new SolidColorBrush(Color.FromRgb(255, 255, 255))
                : SystemColors.HighlightTextBrush;

            var hoverBackground = darkModeEnabled
                ? new SolidColorBrush(Color.FromRgb(65, 65, 65))
                : new SolidColorBrush(Color.FromRgb(210, 210, 210));

            var scrollBarBackground = darkModeEnabled
                ? new SolidColorBrush(Color.FromRgb(40, 40, 40))
                : new SolidColorBrush(Color.FromRgb(240, 240, 240));

            var scrollBarThumb = darkModeEnabled
                ? new SolidColorBrush(Color.FromRgb(90, 90, 90))
                : new SolidColorBrush(Color.FromRgb(180, 180, 180));

            // Update application-level ListView selection colors
            UpdateApplicationLevelListViewColors(selectionBackground, hoverBackground);

            // Window + root grid
            if (window.FindName("mainGrid") is Panel mainGrid)
            {
                mainGrid.Background = background;
            }
            window.Background = background;

            // ListViews - theme the container, headers, scrollbars - but NEVER touch ItemContainerStyle
            // (Row coloring via DataTriggers and Converters is handled in XAML ItemContainerStyle)
            foreach (var listView in FindVisualChildren<ListView>(window))
            {
                // Theme ListView container properties only
                listView.Background = controlBackground;
                listView.Foreground = textBrush;
                listView.BorderBrush = borderBrush;

                // Theme GridView headers if present
                if (listView.View is GridView gridView)
                {
                    foreach (var column in gridView.Columns)
                    {
                        if (column.Header is GridViewColumnHeader headerControl)
                        {
                            headerControl.Background = headerBackground;
                            headerControl.Foreground = textBrush;
                            headerControl.BorderBrush = borderBrush;
                        }
                        else if (column.Header is string headerText)
                        {
                            column.Header = new GridViewColumnHeader
                            {
                                Content = headerText,
                                Background = headerBackground,
                                Foreground = textBrush,
                                BorderBrush = borderBrush
                            };
                        }
                    }
                }

                // Theme scrollbars
                ApplyScrollBarTheme(listView, scrollBarBackground, scrollBarThumb, borderBrush);
            }

            // ListBoxes (like in CoverArtWindow) - but NOT ListViews (ListView inherits from ListBox!)
            foreach (var listBox in FindVisualChildren<ListBox>(window))
            {
                // Skip ListViews - they inherit from ListBox but have their own XAML styling
                if (listBox is ListView)
                    continue;

                listBox.Background = controlBackground;
                listBox.Foreground = textBrush;
                listBox.BorderBrush = borderBrush;

                var itemStyle = new Style(typeof(ListBoxItem));
                itemStyle.Setters.Add(new Setter(ListBoxItem.BackgroundProperty, Brushes.Transparent));
                itemStyle.Setters.Add(new Setter(ListBoxItem.ForegroundProperty, textBrush));
                
                var mouseOverTrigger = new Trigger { Property = ListBoxItem.IsMouseOverProperty, Value = true };
                mouseOverTrigger.Setters.Add(new Setter(ListBoxItem.BackgroundProperty, hoverBackground));
                itemStyle.Triggers.Add(mouseOverTrigger);
               
                var selectedTrigger = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
                selectedTrigger.Setters.Add(new Setter(ListBoxItem.BackgroundProperty, selectionBackground));
                selectedTrigger.Setters.Add(new Setter(ListBoxItem.ForegroundProperty, selectionTextBrush));
                itemStyle.Triggers.Add(selectedTrigger);
                
                listBox.ItemContainerStyle = itemStyle;

                ApplyScrollBarTheme(listBox, scrollBarBackground, scrollBarThumb, borderBrush);
            }
            


            // Labels
            foreach (var label in FindVisualChildren<Label>(window))
            {
                label.Foreground = textBrush;
            }

            // Buttons with hover and disabled-state triggers
            foreach (var button in FindVisualChildren<Button>(window))
            {
                var baseStyle = button.Style;
                var style = new Style(typeof(Button), baseStyle);

                // Normal state
                style.Setters.Add(new Setter(Button.BackgroundProperty, buttonBackground));
                style.Setters.Add(new Setter(Button.ForegroundProperty, textBrush));
                style.Setters.Add(new Setter(Button.BorderBrushProperty, buttonBorder));

                // Hover state - IMPORTANT: Use MultiTrigger to check both hover AND enabled
                var hoverTrigger = new MultiTrigger();
                hoverTrigger.Conditions.Add(new Condition(Button.IsMouseOverProperty, true));
                hoverTrigger.Conditions.Add(new Condition(Button.IsEnabledProperty, true));
                hoverTrigger.Setters.Add(new Setter(Button.BackgroundProperty, hoverBackground));
                hoverTrigger.Setters.Add(new Setter(Button.ForegroundProperty, textBrush));
                style.Triggers.Add(hoverTrigger);

                // Disabled state - takes priority over hover
                var disabledTrigger = new Trigger
                {
                    Property = Button.IsEnabledProperty,
                    Value = false
                };
                disabledTrigger.Setters.Add(new Setter(Button.BackgroundProperty, buttonDisabledBackground));
                disabledTrigger.Setters.Add(new Setter(Button.ForegroundProperty, disabledTextBrush));
                disabledTrigger.Setters.Add(new Setter(Button.BorderBrushProperty, borderBrush));
                style.Triggers.Add(disabledTrigger);

                button.Style = style;
            }

            // Theme buttons inside ItemsControl DataTemplates
            foreach (var itemsControl in FindVisualChildren<ItemsControl>(window))
            {
                ApplyItemsControlButtonTheme(itemsControl, buttonBackground, textBrush, buttonBorder, hoverBackground, buttonDisabledBackground, disabledTextBrush, borderBrush);
            }

            // ComboBoxes with dropdown theming
            foreach (var comboBox in FindVisualChildren<ComboBox>(window))
            {
                var comboStyle = new Style(typeof(ComboBox));
                comboStyle.Setters.Add(new Setter(ComboBox.BackgroundProperty, controlBackground));
                comboStyle.Setters.Add(new Setter(ComboBox.ForegroundProperty, textBrush));
                comboBox.BorderBrush = borderBrush;
                
                // Template to ensure selected item text is visible
                var textBlockStyle = new Style(typeof(TextBlock));
                textBlockStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, textBrush));
                comboStyle.Resources.Add(typeof(TextBlock), textBlockStyle);
                
                // Create item container style for dropdown items
                var itemStyle = new Style(typeof(ComboBoxItem));
                itemStyle.Setters.Add(new Setter(ComboBoxItem.BackgroundProperty, controlBackground));
                itemStyle.Setters.Add(new Setter(ComboBoxItem.ForegroundProperty, textBrush));
                
                // Hover state for dropdown items
                var itemHoverTrigger = new Trigger
                {
                    Property = ComboBoxItem.IsMouseOverProperty,
                    Value = true
                };
                itemHoverTrigger.Setters.Add(new Setter(ComboBoxItem.BackgroundProperty, hoverBackground));
                itemHoverTrigger.Setters.Add(new Setter(ComboBoxItem.ForegroundProperty, textBrush));
                itemStyle.Triggers.Add(itemHoverTrigger);
                
                // Selected state for dropdown items
                var itemSelectedTrigger = new Trigger
                {
                    Property = ComboBoxItem.IsSelectedProperty,
                    Value = true
                };
                itemSelectedTrigger.Setters.Add(new Setter(ComboBoxItem.BackgroundProperty, selectionBackground));
                itemSelectedTrigger.Setters.Add(new Setter(ComboBoxItem.ForegroundProperty, selectionTextBrush));
                itemStyle.Triggers.Add(itemSelectedTrigger);
                
                comboStyle.Resources.Add(typeof(ComboBoxItem), itemStyle);
                comboBox.Style = comboStyle;
                
                // Force immediate update of selected item display
                comboBox.ApplyTemplate();
            }

            // TextBoxes
            foreach (var textBox in FindVisualChildren<TextBox>(window))
            {
                textBox.Background = controlBackground;
                textBox.Foreground = textBrush;
                textBox.BorderBrush = borderBrush;
                textBox.CaretBrush = textBrush;
            }

            // TextBlocks
            foreach (var textBlock in FindVisualChildren<TextBlock>(window))
            {
                textBlock.Foreground = textBrush;
            }

            // DataGrids
            foreach (var dataGrid in FindVisualChildren<DataGrid>(window))
            {
                dataGrid.Background = controlBackground;
                dataGrid.Foreground = textBrush;
                dataGrid.BorderBrush = borderBrush;
                dataGrid.RowBackground = controlBackground;
                dataGrid.AlternatingRowBackground = darkModeEnabled 
                    ? new SolidColorBrush(Color.FromRgb(40, 40, 40))
                    : new SolidColorBrush(Color.FromRgb(250, 250, 250));

                // Column header style
                var headerStyle = new Style(typeof(DataGridColumnHeader));
                headerStyle.Setters.Add(new Setter(DataGridColumnHeader.BackgroundProperty, headerBackground));
                headerStyle.Setters.Add(new Setter(DataGridColumnHeader.ForegroundProperty, textBrush));
                headerStyle.Setters.Add(new Setter(DataGridColumnHeader.BorderBrushProperty, borderBrush));
                headerStyle.Setters.Add(new Setter(DataGridColumnHeader.BorderThicknessProperty, new Thickness(0, 0, 1, 1)));
                dataGrid.ColumnHeaderStyle = headerStyle;

                // Row style for selection
                var rowStyle = new Style(typeof(DataGridRow));
                rowStyle.Setters.Add(new Setter(DataGridRow.BackgroundProperty, Brushes.Transparent));
                rowStyle.Setters.Add(new Setter(DataGridRow.ForegroundProperty, textBrush));
                dataGrid.RowStyle = rowStyle;

                // Cell style
                var cellStyle = new Style(typeof(DataGridCell));
                cellStyle.Setters.Add(new Setter(DataGridCell.BackgroundProperty, Brushes.Transparent));
                cellStyle.Setters.Add(new Setter(DataGridCell.ForegroundProperty, textBrush));
                cellStyle.Setters.Add(new Setter(DataGridCell.BorderBrushProperty, Brushes.Transparent));
                
                // Selected cell style
                var selectedTrigger = new Trigger
                {
                    Property = DataGridCell.IsSelectedProperty,
                    Value = true
                };
                selectedTrigger.Setters.Add(new Setter(DataGridCell.BackgroundProperty, selectionBackground));
                selectedTrigger.Setters.Add(new Setter(DataGridCell.ForegroundProperty, selectionTextBrush));
                cellStyle.Triggers.Add(selectedTrigger);
                
                dataGrid.CellStyle = cellStyle;

                ApplyScrollBarTheme(dataGrid, scrollBarBackground, scrollBarThumb, borderBrush);
            }

            // Toggle icon if present
            if (window.FindName("btn_ToggleDarkMode") is Button toggleBtn && toggleBtn.Content is TextBlock tb)
            {
                tb.Text = darkModeEnabled ? "🌙" : "☀️";
            }
        }

        #endregion

        #region Helper Methods

        private static void ApplyScrollBarTheme(FrameworkElement element, Brush background, Brush thumb, Brush border)
        {
            if (element == null) return;

            var scrollBarStyle = new Style(typeof(ScrollBar));
            scrollBarStyle.Setters.Add(new Setter(ScrollBar.BackgroundProperty, background));
            
            // Thumb style
            var thumbStyle = new Style(typeof(Thumb));
            thumbStyle.Setters.Add(new Setter(Thumb.BackgroundProperty, thumb));
            thumbStyle.Setters.Add(new Setter(Thumb.BorderBrushProperty, border));
            
            scrollBarStyle.Resources.Add(typeof(Thumb), thumbStyle);
            
            // Check if ScrollBar style already exists and remove it before adding new one
            if (element.Resources.Contains(typeof(ScrollBar)))
            {
                element.Resources.Remove(typeof(ScrollBar));
            }
            
            element.Resources.Add(typeof(ScrollBar), scrollBarStyle);
        }

        private static void ApplyItemsControlButtonTheme(ItemsControl itemsControl, Brush buttonBackground, Brush textBrush, Brush buttonBorder, Brush hoverBackground, Brush buttonDisabledBackground, Brush disabledTextBrush, Brush borderBrush)
        {
            if (itemsControl == null || itemsControl.ItemsSource == null) return;

            // Create a button style for the ItemsControl
            var buttonStyle = new Style(typeof(Button));
            
            buttonStyle.Setters.Add(new Setter(Button.BackgroundProperty, buttonBackground));
            buttonStyle.Setters.Add(new Setter(Button.ForegroundProperty, textBrush));
            buttonStyle.Setters.Add(new Setter(Button.BorderBrushProperty, buttonBorder));

            var hoverTrigger = new MultiTrigger();
            hoverTrigger.Conditions.Add(new Condition(Button.IsMouseOverProperty, true));
            hoverTrigger.Conditions.Add(new Condition(Button.IsEnabledProperty, true));
            hoverTrigger.Setters.Add(new Setter(Button.BackgroundProperty, hoverBackground));
            buttonStyle.Triggers.Add(hoverTrigger);

            var disabledTrigger = new Trigger { Property = Button.IsEnabledProperty, Value = false };
            disabledTrigger.Setters.Add(new Setter(Button.BackgroundProperty, buttonDisabledBackground));
            disabledTrigger.Setters.Add(new Setter(Button.ForegroundProperty, disabledTextBrush));
            disabledTrigger.Setters.Add(new Setter(Button.BorderBrushProperty, borderBrush));
            buttonStyle.Triggers.Add(disabledTrigger);

            // Add to ItemsControl resources
            if (itemsControl.Resources.Contains(typeof(Button)))
                itemsControl.Resources.Remove(typeof(Button));
            
            itemsControl.Resources.Add(typeof(Button), buttonStyle);

            // TextBlock style for button content
            var textBlockStyle = new Style(typeof(TextBlock));
            textBlockStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, textBrush));
            
            if (itemsControl.Resources.Contains(typeof(TextBlock)))
                itemsControl.Resources.Remove(typeof(TextBlock));
                
            itemsControl.Resources.Add(typeof(TextBlock), textBlockStyle);
        }

        // Helper to walk visual tree
        private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) yield break;

            int childCount = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < childCount; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typedChild)
                    yield return typedChild;

                foreach (var grandChild in FindVisualChildren<T>(child))
                    yield return grandChild;
            }
        }

        // Helper to enable/disable all buttons (used during busy state)
        public static void SetAllButtonsEnabled(Window window, bool enabled)
        {
            foreach (var button in FindVisualChildren<Button>(window))
            {
                button.IsEnabled = enabled;
            }
        }

        // Update application-level ListView selection colors (affects all ListViews globally)
        private static void UpdateApplicationLevelListViewColors(Brush selectionBrush, Brush hoverBrush)
        {
            try
            {
                var app = Application.Current;
                if (app == null) return;

                // Update or add the selection brush
                if (app.Resources.Contains("ListViewItemSelectionBrush"))
                    app.Resources["ListViewItemSelectionBrush"] = selectionBrush;
                else
                    app.Resources.Add("ListViewItemSelectionBrush", selectionBrush);

                // Update or add the hover brush
                if (app.Resources.Contains("ListViewItemHoverBrush"))
                    app.Resources["ListViewItemHoverBrush"] = hoverBrush;
                else
                    app.Resources.Add("ListViewItemHoverBrush", hoverBrush);
            }
            catch
            {
                // Ignore errors updating application resources
            }
        }

        #endregion

        #region Action Color Converter (consolidated from ActionToBrushConverters.cs)

        /// <summary>
        /// Gets background brush for a ProposalActionType, respecting dark mode and destination colors
        /// </summary>
        public static Brush GetActionBackground(ProposalActionType action, string? destinationName = null)
        {
            var darkMode = AppSettings.Current.GetTyped<bool>(SettingKey.DarkMode);

            // For MoveToDestination, look up the destination color
            if (action == ProposalActionType.MoveToDestination && !string.IsNullOrWhiteSpace(destinationName))
            {
                var dest = AppSettings.Current.ImportDestinations?.FirstOrDefault(d => d.Name == destinationName);
                if (dest != null)
                {
                    var colorKey = darkMode ? dest.ColorDark : dest.Color;
                    return ParseColorBrush(colorKey) ?? Brushes.SkyBlue;
                }
                return Brushes.SkyBlue;
            }

            return action switch
            {
                ProposalActionType.Import => ParseColorBrush(AppSettings.GetValue(darkMode ? SettingKey.ColorImportMatchDark : SettingKey.ColorImportMatch)) ?? Brushes.LightGreen,
                ProposalActionType.NotForImport => ParseColorBrush(AppSettings.GetValue(darkMode ? SettingKey.ColorNotForImportDark : SettingKey.ColorNotForImport)) ?? Brushes.Orange,
                ProposalActionType.Defer => ParseColorBrush(AppSettings.GetValue(darkMode ? SettingKey.ColorDeferDark : SettingKey.ColorDefer)) ?? Brushes.LightGoldenrodYellow,
                ProposalActionType.Unlink => ParseColorBrush(AppSettings.GetValue(darkMode ? SettingKey.ColorUnlinkDark : SettingKey.ColorUnlink)) ?? Brushes.LightSalmon,
                ProposalActionType.Delete => ParseColorBrush(AppSettings.GetValue(darkMode ? SettingKey.ColorDeleteDark : SettingKey.ColorDelete)) ?? Brushes.LightCoral,
                ProposalActionType.MoveToDestination => Brushes.SkyBlue,
                _ => Brushes.Transparent,
            };
        }

        /// <summary>
        /// Gets foreground brush for a ProposalActionType (black or white based on background luminance)
        /// </summary>
        public static Brush GetActionForeground(ProposalActionType action, string? destinationName = null)
        {
            var background = GetActionBackground(action, destinationName);
            return IsDarkColor(background) ? Brushes.White : Brushes.Black;
        }

        private static Brush? ParseColorBrush(string? colorHex)
        {
            if (string.IsNullOrWhiteSpace(colorHex)) return null;
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(colorHex);
                return new SolidColorBrush(color);
            }
            catch
            {
                return null;
            }
        }

        private static bool IsDarkColor(Brush brush)
        {
            if (brush is SolidColorBrush scb)
            {
                var color = scb.Color;
                // Calculate luminance
                double luminance = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255;
                return luminance < 0.5;
            }
            return false;
        }

        /// <summary>
        /// Gets themed color for Artist Release Tracks list colors (HasFile, ReleaseHasAssigned, Assigned)
        /// </summary>
        public static Brush GetThemedColor(string colorName)
        {
            var darkMode = AppSettings.Current.GetTyped<bool>(SettingKey.DarkMode);
            
            return colorName.ToLowerInvariant() switch
            {
                "trackhasfile" or "lightgray" => ParseColorBrush(AppSettings.GetValue(darkMode ? SettingKey.ColorTrackHasFileDark : SettingKey.ColorTrackHasFile)) ?? Brushes.LightGray,
                "releasehasassigned" or "lightblue" => ParseColorBrush(AppSettings.GetValue(darkMode ? SettingKey.ColorReleaseHasAssignedDark : SettingKey.ColorReleaseHasAssigned)) ?? Brushes.LightBlue,
                "trackassigned" or "lightgreen" => ParseColorBrush(AppSettings.GetValue(darkMode ? SettingKey.ColorTrackAssignedDark : SettingKey.ColorTrackAssigned)) ?? Brushes.LightGreen,
                _ => Brushes.Transparent
            };
        }

        /// <summary>
        /// Convenient properties for themed colors
        /// </summary>
        public static Brush TrackHasFileColor => GetThemedColor("trackhasfile");
        public static Brush ReleaseHasAssignedColor => GetThemedColor("releasehasassigned");
        public static Brush TrackAssignedColor => GetThemedColor("trackassigned");

        #endregion
    }

    #region Action Color Converters for XAML Binding

    /// <summary>
    /// WPF Value Converter for ProposalActionType background colors
    /// </summary>
    public class ActionToBackgroundConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            try
            {
                if (value == null)
                    return Brushes.Transparent;

                if (value is ProposedAction proposedAction)
                {
                    return ThemeManager.GetActionBackground(proposedAction.Action, proposedAction.DestinationName);
                }
                else if (value is ProposalActionType actionType)
                {
                    return ThemeManager.GetActionBackground(actionType);
                }

                // Try to handle if value is a nullable enum
                var valueType = value.GetType();
                if (valueType.IsEnum || (Nullable.GetUnderlyingType(valueType)?.IsEnum ?? false))
                {
                    if (Enum.TryParse(value.ToString(), out ProposalActionType parsedType))
                    {
                        return ThemeManager.GetActionBackground(parsedType);
                    }
                }

                return Brushes.Transparent;
            }
            catch
            {
                return Brushes.Transparent;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) 
            => throw new NotSupportedException();
    }

    /// <summary>
    /// WPF Value Converter for ProposalActionType foreground colors
    /// </summary>
    public class ActionToForegroundConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            try
            {
                if (value == null)
                    return Brushes.Black;

                if (value is ProposedAction proposedAction)
                {
                    return ThemeManager.GetActionForeground(proposedAction.Action, proposedAction.DestinationName);
                }
                else if (value is ProposalActionType actionType)
                {
                    return ThemeManager.GetActionForeground(actionType);
                }

                // Try to handle if value is a nullable enum
                var valueType = value.GetType();
                if (valueType.IsEnum || (Nullable.GetUnderlyingType(valueType)?.IsEnum ?? false))
                {
                    if (Enum.TryParse(value.ToString(), out ProposalActionType parsedType))
                    {
                        return ThemeManager.GetActionForeground(parsedType);
                    }
                }

                return Brushes.Black;
            }
            catch
            {
                return Brushes.Black;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) 
            => throw new NotSupportedException();
    }

    /// <summary>
    /// WPF Value Converter for themed Artist Release Track colors (dark mode aware)
    /// Usage: Binding with ConverterParameter="TrackHasFile", "ReleaseHasAssigned", or "TrackAssigned"
    /// </summary>
    public class ThemedColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var colorName = parameter?.ToString() ?? "";
            return ThemeManager.GetThemedColor(colorName);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) 
            => throw new NotSupportedException();
    }

    #endregion
}




