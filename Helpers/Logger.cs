using LidarrCompanion.Models;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;

namespace LidarrCompanion.Helpers
{
    public static class Logger
    {
        private static ObservableCollection<LogEntry> _logEntries = new();
        private static readonly int MaxLogEntries = 1000;
        private static object _lock = new object();

        public static ObservableCollection<LogEntry> LogEntries => _logEntries;

        public static void Log(string description, LogSeverity severity = LogSeverity.Medium, object? data = null, string? filePath = null, [CallerMemberName] string callerMethod = "", [CallerFilePath] string callerFilePath = "")
        {
            try
            {
                var timestamp = DateTime.Now;
                
                // Extract just the method and class name from the caller information
                var methodName = string.IsNullOrEmpty(callerMethod) ? "Unknown" : callerMethod;
                var fileName = System.IO.Path.GetFileNameWithoutExtension(callerFilePath);
                var fullMethodName = $"{fileName}.{methodName}";

                var entry = new LogEntry(timestamp, fullMethodName, description, severity, data, filePath);

                // Use dispatcher to ensure thread-safe updates to the observable collection
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    lock (_lock)
                    {
                        // Add at the beginning (newest first)
                        _logEntries.Insert(0, entry);

                        // Remove oldest entries if we exceed max
                        while (_logEntries.Count > MaxLogEntries)
                        {
                            _logEntries.RemoveAt(_logEntries.Count - 1);
                        }
                    }
                });

                // Also write to debug output for convenience
                var filePathInfo = !string.IsNullOrWhiteSpace(filePath) ? $" [File: {System.IO.Path.GetFileName(filePath)}]" : "";
                Debug.WriteLine($"[{entry.FormattedTimestamp}] [{severity}] {fullMethodName}: {description}{filePathInfo}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Logger error: {ex.Message}");
            }
        }

        public static void Clear()
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                lock (_lock)
                {
                    _logEntries.Clear();
                }
            });
        }
    }
}
