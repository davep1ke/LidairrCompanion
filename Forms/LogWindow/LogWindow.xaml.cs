using LidarrCompanion.Helpers;
using LidarrCompanion.Models;
using LidarrCompanion.Services;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace LidarrCompanion
{
    public partial class LogWindow : Window
    {
        private ICollectionView _logEntriesView;
        private bool _allowClose = false;

        public LogWindow()
        {
            InitializeComponent();
            
            // Bind to the logger's collection
            _logEntriesView = CollectionViewSource.GetDefaultView(Logger.LogEntries);
            _logEntriesView.Filter = FilterLogEntry;
            dg_LogEntries.ItemsSource = _logEntriesView;
            
            // Listen for changes to update count
            Logger.LogEntries.CollectionChanged += LogEntries_CollectionChanged;
            
            UpdateLogCount();
            UpdateSeverityLabel();
            
            // Apply theme
            Loaded += (s, e) => Helpers.ThemeManager.ApplyTheme(this);
        }

        public void AllowClose()
        {
            _allowClose = true;
        }

        private void LogEntries_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            UpdateLogCount();
        }

        private bool FilterLogEntry(object obj)
        {
            if (obj is LogEntry entry)
            {
                var minSeverity = (LogSeverity)sldr_MinSeverity.Value;
                return entry.Severity >= minSeverity;
            }
            return true;
        }

        private void sldr_MinSeverity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            UpdateSeverityLabel();
            _logEntriesView?.Refresh();
            UpdateLogCount();
        }

        private void UpdateSeverityLabel()
        {
            if (lbl_SeverityValue != null)
            {
                var severity = (LogSeverity)sldr_MinSeverity.Value;
                lbl_SeverityValue.Content = severity.ToString();
            }
        }

        private void UpdateLogCount()
        {
            if (txt_LogCount != null && _logEntriesView != null)
            {
                var filteredCount = _logEntriesView.Cast<object>().Count();
                var totalCount = Logger.LogEntries.Count;
                txt_LogCount.Text = $"{filteredCount} of {totalCount} entries";
            }
        }

        private void btn_ClearLog_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "Are you sure you want to clear all log entries?",
                "Clear Log",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            
            if (result == MessageBoxResult.Yes)
            {
                Logger.Clear();
                UpdateLogCount();
            }
        }

        private void btn_OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string filePath && !string.IsNullOrWhiteSpace(filePath))
            {
                try
                {
                    // Use FileAndAudioService to open the folder with the file selected
                    FileAndAudioService.OpenContainingFolder(filePath, null, null);
                }
                catch (System.Exception ex)
                {
                    MessageBox.Show($"Failed to open folder: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    Logger.Log($"Failed to open folder for log entry: {ex.Message}", LogSeverity.Medium, filePath: filePath);
                }
            }
        }

        private void btn_CopyUrl_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string url && !string.IsNullOrWhiteSpace(url))
            {
                try
                {
                    Clipboard.SetText(url);
                    Logger.Log($"URL copied to clipboard", LogSeverity.Verbose, filePath: url);
                    
                    // Show brief confirmation (optional - you could use a status bar instead)
                    MessageBox.Show($"URL copied to clipboard:\n{url}", "Copied", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (System.Exception ex)
                {
                    MessageBox.Show($"Failed to copy URL to clipboard: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    Logger.Log($"Failed to copy URL to clipboard: {ex.Message}", LogSeverity.Medium, filePath: url);
                }
            }
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            // Allow close if explicitly requested (app shutdown) or cancel to just hide
            if (!_allowClose)
            {
                e.Cancel = true;
                Hide();
            }
        }
    }
}
