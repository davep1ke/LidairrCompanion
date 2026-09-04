using LidarrCompanion.Services;

namespace LidarrCompanion.Core.Tests.Services
{
    public class FileAndAudioServiceTests
    {
        [Theory]
        [InlineData("The Beatles - Abbey Road!", "the beatles abbey road")]
        [InlineData("  Multiple   Spaces  ", "multiple spaces")]
        [InlineData("Song (Remastered)", "song remastered")]
        public void Normalize_LowercasesStripsPunctuationAndCollapsesWhitespace(string input, string expected)
        {
            Assert.Equal(expected, FileAndAudioService.Normalize(input));
        }

        [Fact]
        public void Normalize_NullOrWhitespace_ReturnsEmpty()
        {
            Assert.Equal(string.Empty, FileAndAudioService.Normalize(""));
            Assert.Equal(string.Empty, FileAndAudioService.Normalize("   "));
        }

        [Fact]
        public void GetLowestFolderName_ForFolderPath_ReturnsLastSegment()
        {
            Assert.Equal("Album", FileAndAudioService.GetLowestFolderName("/mnt/music/Artist/Album"));
        }

        [Fact]
        public void GetLowestFolderName_ForFilePath_ReturnsParentFolderName()
        {
            Assert.Equal("Album", FileAndAudioService.GetLowestFolderName("/mnt/music/Artist/Album/track01.mp3"));
        }

        [Fact]
        public void GetLowestFolderName_NullOrWhitespace_ReturnsNull()
        {
            Assert.Null(FileAndAudioService.GetLowestFolderName(null));
            Assert.Null(FileAndAudioService.GetLowestFolderName("  "));
        }

        [Fact]
        public void NormalizePathForComparison_TrimsTrailingSeparator()
        {
            Assert.Equal("/mnt/music/Artist", FileAndAudioService.NormalizePathForComparison("/mnt/music/Artist/"));
        }

        [Fact]
        public void NormalizePathForComparison_Null_ReturnsEmpty()
        {
            Assert.Equal(string.Empty, FileAndAudioService.NormalizePathForComparison(null));
        }

        [Fact]
        public void IsSingleFileRelease_OutputEqualsImportPath_ReturnsTrue()
        {
            Assert.True(FileAndAudioService.IsSingleFileRelease("/mnt/import", "/mnt/import"));
        }

        [Fact]
        public void IsSingleFileRelease_FileDirectlyInImportPath_ReturnsTrue()
        {
            Assert.True(FileAndAudioService.IsSingleFileRelease("/mnt/import/track.mp3", "/mnt/import"));
        }

        [Fact]
        public void IsSingleFileRelease_FolderUnderImportPath_ReturnsFalse()
        {
            Assert.False(FileAndAudioService.IsSingleFileRelease("/mnt/import/AlbumFolder", "/mnt/import"));
        }

        [Fact]
        public void IsSingleFileRelease_MissingImportPath_ReturnsFalse()
        {
            Assert.False(FileAndAudioService.IsSingleFileRelease("/mnt/import/track.mp3", null));
        }
    }
}
