using LidarrCompanion.Helpers;

namespace LidarrCompanion.Core.Tests.Helpers
{
    // Uses real temp directories rather than a fake filesystem - fast, deterministic, and this is
    // exactly the class of check (does a real Directory.Exists/write actually work) that a mock
    // filesystem would just assert away rather than test.
    public class FolderAccessCheckTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"folder-access-check-{Guid.NewGuid():N}");

        public FolderAccessCheckTests() => Directory.CreateDirectory(_root);
        public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

        [Fact]
        public void BlankPath_IsOk_NothingToCheck()
        {
            var result = FolderAccessCheck.Check("Sift", "", requireWrite: true);

            Assert.True(result.Ok);
            Assert.Null(result.Error);
        }

        [Fact]
        public void NullPath_IsOk_NothingToCheck()
        {
            Assert.True(FolderAccessCheck.Check("Sift", null, requireWrite: true).Ok);
        }

        [Fact]
        public void MissingFolder_Fails()
        {
            var result = FolderAccessCheck.Check("Backup", Path.Combine(_root, "does-not-exist"), requireWrite: false);

            Assert.False(result.Ok);
            Assert.Contains("not found", result.Error);
        }

        [Fact]
        public void ExistingReadableFolder_ReadOnlyCheck_Passes()
        {
            var result = FolderAccessCheck.Check("Library", _root, requireWrite: false);

            Assert.True(result.Ok);
            Assert.Null(result.Error);
        }

        [Fact]
        public void ExistingWritableFolder_WriteCheck_Passes_AndLeavesNoProbeFileBehind()
        {
            var result = FolderAccessCheck.Check("Backup", _root, requireWrite: true);

            Assert.True(result.Ok);
            Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
        }

        [Fact]
        public void ReadOnlyFolder_WriteCheck_Fails()
        {
            var dir = new DirectoryInfo(_root);
            dir.Attributes |= FileAttributes.ReadOnly;
            try
            {
                // On Linux, DirectoryInfo's ReadOnly attribute doesn't block writes the way it does
                // on Windows - use a real permission change instead, skipping the check on a
                // platform where neither mechanism applies (defensive; this test only runs where
                // Unix permissions are meaningful).
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(_root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                }

                var result = FolderAccessCheck.Check("Backup", _root, requireWrite: true);

                Assert.False(result.Ok);
                Assert.NotNull(result.Error);
            }
            finally
            {
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(_root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                dir.Attributes &= ~FileAttributes.ReadOnly;
            }
        }

        [Fact]
        public void Label_AndPath_AreCarriedThroughOnFailure()
        {
            var missing = Path.Combine(_root, "gone");
            var result = FolderAccessCheck.Check("Backup Root Folder", missing, requireWrite: true);

            Assert.Equal("Backup Root Folder", result.Label);
            Assert.Equal(missing, result.Path);
        }
    }
}
