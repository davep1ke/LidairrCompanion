using LidarrCompanion.Helpers;
using LidarrCompanion.Models;

namespace LidarrCompanion.Web.Services
{
    public interface ILogService
    {
        IReadOnlyList<LogEntry> Entries { get; }

        // Fired on every new entry so a Blazor component can InvokeAsync(StateHasChanged).
        event Action<LogEntry>? EntryAdded;
    }

    // Bridges the Core Logger (a plain static event, so it has no Blazor/hosting dependencies)
    // to persistence and the live UI: appends every entry to a rolling per-day text file on the
    // mounted data volume, and re-raises the event for the Logs page to subscribe to.
    public sealed class LogService : ILogService, IDisposable
    {
        private const int RetentionDays = 30;

        private readonly string _logDirectory;
        private readonly object _fileLock = new();

        public LogService(IConfiguration configuration, IHostEnvironment env)
        {
            var configured = configuration["LogsDirectory"];
            _logDirectory = !string.IsNullOrWhiteSpace(configured)
                ? (Path.IsPathRooted(configured) ? configured : Path.Combine(env.ContentRootPath, configured))
                : Path.Combine(AppSettings.DataDirectory, "logs");

            Directory.CreateDirectory(_logDirectory);
            PruneOldLogFiles();

            Logger.EntryLogged += OnEntryLogged;
        }

        public IReadOnlyList<LogEntry> Entries => Logger.LogEntries;

        public event Action<LogEntry>? EntryAdded;

        private void OnEntryLogged(LogEntry entry)
        {
            EntryAdded?.Invoke(entry);
            AppendToFile(entry);
        }

        private void AppendToFile(LogEntry entry)
        {
            var path = Path.Combine(_logDirectory, $"lidarrcompanion-{DateTime.Now:yyyy-MM-dd}.log");
            var filePart = entry.HasFilePath ? $" [File: {entry.FilePath}]" : string.Empty;
            var line = $"[{entry.FormattedTimestamp}] [{entry.SeverityDisplay}] {entry.Method}: {entry.Description}{filePart}";

            lock (_fileLock)
            {
                try
                {
                    File.AppendAllText(path, line + Environment.NewLine);
                }
                catch
                {
                    // Best-effort: a failed write to the log file shouldn't take down the app.
                }
            }
        }

        private void PruneOldLogFiles()
        {
            try
            {
                var cutoff = DateTime.Now.AddDays(-RetentionDays);
                foreach (var file in Directory.EnumerateFiles(_logDirectory, "lidarrcompanion-*.log"))
                {
                    if (File.GetLastWriteTime(file) < cutoff)
                    {
                        try { File.Delete(file); } catch { }
                    }
                }
            }
            catch
            {
                // Non-fatal - pruning is a housekeeping nicety, not required for correctness.
            }
        }

        public void Dispose()
        {
            Logger.EntryLogged -= OnEntryLogged;
        }
    }
}
