using LidarrCompanion.Helpers;
using Xunit;

namespace LidarrCompanion.Core.Tests.Helpers
{
    public class BackupPathHelperTests
    {
        [Fact]
        public void ComputeBackupFilePath_CombinesRootFolderAndFileName()
        {
            var result = BackupPathHelper.ComputeBackupFilePath(
                "/mnt/backup", "Some Release", "/mnt/Music/1ToClean/Some Release/track.m4a");

            Assert.Equal(Path.Combine("/mnt/backup", "Some Release", "track.m4a"), result);
        }

        [Fact]
        public void ComputeBackupFilePath_UsesOnlyFileNameFromSourcePath()
        {
            var result = BackupPathHelper.ComputeBackupFilePath(
                "/mnt/backup", "Some Release", "/mnt/Music/1ToClean/deeply/nested/track.mp3");

            Assert.Equal(Path.Combine("/mnt/backup", "Some Release", "track.mp3"), result);
        }

        [Fact]
        public void ComputeBackupFilePath_IsStableForRoundTripBetweenBackupAndRestore()
        {
            // A single-file release's own folder name (what BackupReleaseGroup falls back to when
            // OriginalRelease is blank) must be reproducible from the file's own path alone, since
            // that's all a restore has to go on for that case.
            const string sourcePath = "/mnt/Music/1ToClean/Dead Serious (Malo Remix).m4a";
            var folderName = Path.GetFileName(sourcePath);

            var written = BackupPathHelper.ComputeBackupFilePath("/mnt/backup", folderName, sourcePath);
            var readBack = BackupPathHelper.ComputeBackupFilePath("/mnt/backup", folderName, sourcePath);

            Assert.Equal(written, readBack);
        }
    }
}
