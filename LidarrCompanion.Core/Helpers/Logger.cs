using LidarrCompanion.Models;
using System.Runtime.CompilerServices;

namespace LidarrCompanion.Helpers
{
    // Platform-neutral replacement for the WPF Logger (which marshalled updates via
    // Application.Current.Dispatcher). Call signature is preserved exactly so the ported
    // Core code (FileOperationsHelper, LidarrHelper, etc.) needs no changes at call sites.
    //
    // EntryLogged lets a host (Blazor's ILogService, a test, etc.) subscribe for live updates
    // and/or file persistence without this class knowing anything about its consumers.
    public static class Logger
    {
        private static readonly List<LogEntry> _logEntries = new();
        private static readonly object _lock = new();
        private const int MaxLogEntries = 1000;

        public static event Action<LogEntry>? EntryLogged;

        public static IReadOnlyList<LogEntry> LogEntries
        {
            get
            {
                lock (_lock)
                {
                    return _logEntries.ToList();
                }
            }
        }

        public static void Log(string description, LogSeverity severity = LogSeverity.Medium, object? data = null, string? filePath = null, [CallerMemberName] string callerMethod = "", [CallerFilePath] string callerFilePath = "")
        {
            var methodName = string.IsNullOrEmpty(callerMethod) ? "Unknown" : callerMethod;
            var fileName = System.IO.Path.GetFileNameWithoutExtension(callerFilePath);
            var fullMethodName = $"{fileName}.{methodName}";

            var entry = new LogEntry(DateTime.Now, fullMethodName, description, severity, data, filePath);

            lock (_lock)
            {
                _logEntries.Insert(0, entry);
                while (_logEntries.Count > MaxLogEntries)
                {
                    _logEntries.RemoveAt(_logEntries.Count - 1);
                }
            }

            EntryLogged?.Invoke(entry);
        }

        public static void Clear()
        {
            lock (_lock)
            {
                _logEntries.Clear();
            }
        }
    }
}
