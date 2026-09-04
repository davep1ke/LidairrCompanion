using LidarrCompanion.Helpers;
using LidarrCompanion.Models;

namespace LidarrCompanion.Core.Tests.Helpers
{
    // Logger is a new WPF-free replacement for the original Dispatcher-based Logger (same call
    // signature, but a plain event instead of Application.Current.Dispatcher.Invoke) - worth
    // testing directly since there's no WPF original to have already exercised this behavior.
    public class LoggerTests : IDisposable
    {
        public LoggerTests()
        {
            Logger.Clear();
        }

        public void Dispose()
        {
            Logger.Clear();
        }

        [Fact]
        public void Log_AddsEntryToLogEntriesNewestFirst()
        {
            Logger.Log("first", LogSeverity.Low);
            Logger.Log("second", LogSeverity.Medium);

            var entries = Logger.LogEntries;

            Assert.Equal("second", entries[0].Description);
            Assert.Equal("first", entries[1].Description);
        }

        [Fact]
        public void Log_RaisesEntryLoggedEvent()
        {
            LogEntry? captured = null;
            void Handler(LogEntry e) => captured = e;
            Logger.EntryLogged += Handler;

            try
            {
                Logger.Log("hello", LogSeverity.High);
            }
            finally
            {
                Logger.EntryLogged -= Handler;
            }

            Assert.NotNull(captured);
            Assert.Equal("hello", captured!.Description);
            Assert.Equal(LogSeverity.High, captured.Severity);
        }

        [Fact]
        public void Clear_RemovesAllEntries()
        {
            Logger.Log("something");
            Logger.Clear();

            Assert.Empty(Logger.LogEntries);
        }
    }
}
