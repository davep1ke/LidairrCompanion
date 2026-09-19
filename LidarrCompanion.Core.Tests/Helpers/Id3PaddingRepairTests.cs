using LidarrCompanion.Helpers;
using LidarrCompanion.Services;

namespace LidarrCompanion.Core.Tests.Helpers
{
    public class Id3PaddingRepairTests
    {
        private static byte[] SynchSafe(long n) => new[] { (byte)((n >> 21) & 0x7F), (byte)((n >> 14) & 0x7F), (byte)((n >> 7) & 0x7F), (byte)(n & 0x7F) };

        // ID3v2.4 tag (tagSize bytes of zero body), then `padding` extra zero bytes, then MPEG1 Layer III
        // 128kbps/44.1kHz frames (417 bytes each) - the shape of the real file that TagLib rejected.
        private static byte[] BuildMp3(int tagSize, int padding, int frames = 8, byte flags = 0)
        {
            var bytes = new List<byte> { (byte)'I', (byte)'D', (byte)'3', 4, 0, flags };
            bytes.AddRange(SynchSafe(tagSize));
            bytes.AddRange(new byte[tagSize]);
            bytes.AddRange(new byte[padding]);
            for (var i = 0; i < frames; i++)
            {
                bytes.AddRange(new byte[] { 0xFF, 0xFB, 0x90, 0x00 });
                bytes.AddRange(new byte[413]);
            }
            return bytes.ToArray();
        }

        private static long ReadSize(byte[] b) => (b[6] << 21) | (b[7] << 14) | (b[8] << 7) | b[9];

        [Fact]
        public void PaddingAfterTheTag_IsAbsorbedIntoTheTagSize()
        {
            var data = BuildMp3(tagSize: 2000, padding: 30000);
            using var stream = new MemoryStream(data.ToArray(), writable: true);

            Assert.True(Id3PaddingRepair.TryExtendTagOverPadding(stream));

            Assert.Equal(32000, ReadSize(stream.ToArray()));
        }

        [Fact]
        public void OnlyTheFourSizeBytesChange()
        {
            var data = BuildMp3(2000, 30000);
            using var stream = new MemoryStream(data.ToArray(), writable: true);

            Id3PaddingRepair.TryExtendTagOverPadding(stream);
            var after = stream.ToArray();

            Assert.Equal(data.Length, after.Length);
            for (var i = 0; i < data.Length; i++)
                if (i < 6 || i > 9) Assert.Equal(data[i], after[i]);
        }

        [Fact]
        public void NoPadding_IsLeftAlone()
        {
            using var stream = new MemoryStream(BuildMp3(2000, 0), writable: true);

            Assert.False(Id3PaddingRepair.TryExtendTagOverPadding(stream));
            Assert.Equal(2000, ReadSize(stream.ToArray()));
        }

        [Fact]
        public void PaddingNotFollowedByAnMpegFrame_IsLeftAlone()
        {
            var data = BuildMp3(2000, 500, frames: 0).Concat(new byte[] { 0x12, 0x34, 0x56, 0x78 }).ToArray();
            using var stream = new MemoryStream(data, writable: true);

            Assert.False(Id3PaddingRepair.TryExtendTagOverPadding(stream));
        }

        [Fact]
        public void NoId3Header_IsLeftAlone()
        {
            using var stream = new MemoryStream(new byte[] { 0xFF, 0xFB, 0x90, 0x00, 0, 0, 0, 0, 0, 0, 0, 0 }, writable: true);

            Assert.False(Id3PaddingRepair.TryExtendTagOverPadding(stream));
        }

        [Fact]
        public void TagWithAFooter_IsLeftAlone()
        {
            using var stream = new MemoryStream(BuildMp3(2000, 30000, flags: 0x10), writable: true);

            Assert.False(Id3PaddingRepair.TryExtendTagOverPadding(stream));
        }

        [Fact]
        public void ReadOnlyStream_IsLeftAlone()
        {
            using var stream = new MemoryStream(BuildMp3(2000, 30000), writable: false);

            Assert.False(Id3PaddingRepair.TryExtendTagOverPadding(stream));
        }

        [Fact]
        public void SavingCoverArt_WorksOnAFileTagLibCouldNotOpenBecauseOfThePadding()
        {
            var path = Path.Combine(Path.GetTempPath(), $"padded-{Guid.NewGuid():N}.mp3");
            try
            {
                File.WriteAllBytes(path, BuildMp3(tagSize: 2000, padding: 40000));

                // Sanity: this really is the failing shape, or the test proves nothing.
                Assert.Throws<TagLib.CorruptFileException>(() => TagLib.File.Create(path));

                var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 16, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0, 0xFF, 0xD9 };
                Assert.True(FileAndAudioService.TrySaveCoverArt(path, jpeg, out var error), error);

                using var reopened = TagLib.File.Create(path);
                Assert.Single(reopened.Tag.Pictures);
                Assert.Equal(TagLib.PictureType.FrontCover, reopened.Tag.Pictures[0].Type);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void SaveFailure_ReportsTheReason()
        {
            Assert.False(FileAndAudioService.TrySaveCoverArt("/definitely/not/here.mp3", new byte[] { 1 }, out var error));
            Assert.Contains("doesn't exist", error);
        }
    }
}
