using LidarrCompanion.Helpers;

namespace LidarrCompanion.Core.Tests.Helpers
{
    public class BackupFailureHelpTests
    {
        [Fact]
        public void MissingFile_GetsTheRestartHint()
        {
            var text = BackupFailureHelp.Describe("Backup failed: Proposed action for release 'Signum': File not found: '/mnt/Music/x.mp3'.");

            Assert.Contains("File not found", text);
            Assert.Contains("restart the container", text);
        }

        [Fact]
        public void OtherFailures_AreLeftAlone()
        {
            const string message = "Backup failed: Access to the path '/mnt/lidarr-backup/x' is denied.";

            Assert.Equal(message, BackupFailureHelp.Describe(message));
        }

        [Fact]
        public void EmptyMessage_FallsBackToAGenericOne()
        {
            Assert.Equal("Backup failed.", BackupFailureHelp.Describe(null));
            Assert.Equal("Backup failed.", BackupFailureHelp.Describe("  "));
        }
    }
}
