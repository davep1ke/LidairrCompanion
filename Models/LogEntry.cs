using System;
using System.ComponentModel;

namespace LidarrCompanion.Models
{
    public class LogEntry : INotifyPropertyChanged
    {
        public DateTime Timestamp { get; set; }
        public string Method { get; set; }
        public string Description { get; set; }
        public LogSeverity Severity { get; set; }
        public object? Data { get; set; }
        public string? FilePath { get; set; }

        public string FormattedTimestamp => Timestamp.ToString("HH:mm:ss.fff");
        
        public string SeverityDisplay => Severity.ToString();

        public bool HasFilePath => !string.IsNullOrWhiteSpace(FilePath);

        public bool IsUrl => !string.IsNullOrWhiteSpace(FilePath) && 
                            (FilePath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || 
                             FilePath.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

        public event PropertyChangedEventHandler? PropertyChanged;

        public LogEntry(DateTime timestamp, string method, string description, LogSeverity severity, object? data = null, string? filePath = null)
        {
            Timestamp = timestamp;
            Method = method;
            Description = description;
            Severity = severity;
            Data = data;
            FilePath = filePath;
        }
    }

    public enum LogSeverity
    {
        Verbose = 0,
        Low = 1,
        Medium = 2,
        High = 3,
        Critical = 4
    }
}
