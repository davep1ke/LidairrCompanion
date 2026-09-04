using LidarrCompanion.Helpers;
using LidarrCompanion.Models;

namespace LidarrCompanion.Core.Tests.Helpers
{
    // Exercises FileOperationsHelper against real temp files/dirs (it's a thin wrapper over
    // System.IO with fallback strategies, so the value is in verifying actual filesystem behavior).
    public class FileOperationsHelperTests : IDisposable
    {
        private readonly string _root;

        public FileOperationsHelperTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "LidarrCompanionTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        }

        [Fact]
        public void ResolveMappedPath_ServerToLocal_MapsPrefixCorrectly()
        {
            AppSettings.Current.Settings[SettingKey.ImportPathLidarr.ToString()] = "/data/import";
            AppSettings.Current.Settings[SettingKey.ImportPathCompanion.ToString()] = "/mnt/local/import";

            var resolved = FileOperationsHelper.ResolveMappedPath("/data/import/Artist/file.mp3", SettingKey.ImportPathLidarr, serverToLocal: true);

            Assert.Equal(Path.Combine("/mnt/local/import", "Artist", "file.mp3"), resolved);
        }

        [Fact]
        public void ResolveMappedPath_LocalToServer_MapsPrefixCorrectly()
        {
            AppSettings.Current.Settings[SettingKey.ImportPathLidarr.ToString()] = "/data/import";
            AppSettings.Current.Settings[SettingKey.ImportPathCompanion.ToString()] = "/mnt/local/import";

            var resolved = FileOperationsHelper.ResolveMappedPath("/mnt/local/import/Artist/file.mp3", SettingKey.ImportPathLidarr, serverToLocal: false);

            Assert.Equal("/data/import/Artist/file.mp3", resolved);
        }

        [Fact]
        public void ResolveMappedPath_NoMappingConfigured_ReturnsOriginalPath()
        {
            AppSettings.Current.Settings[SettingKey.ImportPathLidarr.ToString()] = string.Empty;
            AppSettings.Current.Settings[SettingKey.ImportPathCompanion.ToString()] = string.Empty;

            var original = "/data/import/Artist/file.mp3";
            var resolved = FileOperationsHelper.ResolveMappedPath(original, SettingKey.ImportPathLidarr, serverToLocal: true);

            Assert.Equal(original, resolved);
        }

        [Fact]
        public void MoveFileToDestination_MovesFileAndRemovesSource()
        {
            var sourceDir = Path.Combine(_root, "source");
            var destDir = Path.Combine(_root, "dest");
            Directory.CreateDirectory(sourceDir);
            var sourcePath = Path.Combine(sourceDir, "track.mp3");
            File.WriteAllText(sourcePath, "audio-bytes");

            var resultPath = FileOperationsHelper.MoveFileToDestination(sourcePath, destDir);

            Assert.False(File.Exists(sourcePath));
            Assert.True(File.Exists(resultPath));
            Assert.Equal("audio-bytes", File.ReadAllText(resultPath));
        }

        [Fact]
        public void MoveFileToDestination_IdenticalFileAlreadyAtDestination_RemovesSourceKeepsDestination()
        {
            var sourceDir = Path.Combine(_root, "source");
            var destDir = Path.Combine(_root, "dest");
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(destDir);
            var sourcePath = Path.Combine(sourceDir, "track.mp3");
            var destPath = Path.Combine(destDir, "track.mp3");
            File.WriteAllText(sourcePath, "same-content");
            File.WriteAllText(destPath, "same-content");

            var resultPath = FileOperationsHelper.MoveFileToDestination(sourcePath, destDir);

            Assert.False(File.Exists(sourcePath));
            Assert.Equal(destPath, resultPath);
            Assert.Equal("same-content", File.ReadAllText(destPath));
        }

        [Fact]
        public void CopyFile_CopiesAndLeavesSourceIntact()
        {
            var sourceDir = Path.Combine(_root, "source");
            var destDir = Path.Combine(_root, "dest");
            Directory.CreateDirectory(sourceDir);
            var sourcePath = Path.Combine(sourceDir, "track.mp3");
            File.WriteAllText(sourcePath, "audio-bytes");

            var success = FileOperationsHelper.CopyFile(sourcePath, destDir);

            Assert.True(success);
            Assert.True(File.Exists(sourcePath));
            Assert.True(File.Exists(Path.Combine(destDir, "track.mp3")));
        }

        [Fact]
        public void CopyFile_SourceMissing_ReturnsFalse()
        {
            var success = FileOperationsHelper.CopyFile(Path.Combine(_root, "does-not-exist.mp3"), Path.Combine(_root, "dest"));
            Assert.False(success);
        }

        [Fact]
        public void EnsureDirectoryExists_CreatesMissingDirectory()
        {
            var dir = Path.Combine(_root, "new-dir");
            Assert.False(Directory.Exists(dir));

            var result = FileOperationsHelper.EnsureDirectoryExists(dir);

            Assert.True(result);
            Assert.True(Directory.Exists(dir));
        }

        [Fact]
        public void TryDeleteEmptyDirectory_EmptyDirectory_DeletesAndReturnsTrue()
        {
            var dir = Path.Combine(_root, "empty-dir");
            Directory.CreateDirectory(dir);

            var result = FileOperationsHelper.TryDeleteEmptyDirectory(dir);

            Assert.True(result);
            Assert.False(Directory.Exists(dir));
        }

        [Fact]
        public void TryDeleteEmptyDirectory_NonEmptyDirectory_ReturnsFalseAndKeepsDirectory()
        {
            var dir = Path.Combine(_root, "non-empty-dir");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "file.txt"), "x");

            var result = FileOperationsHelper.TryDeleteEmptyDirectory(dir);

            Assert.False(result);
            Assert.True(Directory.Exists(dir));
        }

        [Fact]
        public void ValidateFileExists_ExistingFile_ReturnsTrue()
        {
            var path = Path.Combine(_root, "file.txt");
            File.WriteAllText(path, "x");
            Assert.True(FileOperationsHelper.ValidateFileExists(path));
        }

        [Fact]
        public void ValidateFileExists_MissingFile_ReturnsFalse()
        {
            Assert.False(FileOperationsHelper.ValidateFileExists(Path.Combine(_root, "missing.txt")));
        }

        [Fact]
        public void ValidateIsFile_Directory_ReturnsFalseWithMessage()
        {
            var ok = FileOperationsHelper.ValidateIsFile(_root, out var error);
            Assert.False(ok);
            Assert.Contains("directory", error, StringComparison.OrdinalIgnoreCase);
        }
    }
}
