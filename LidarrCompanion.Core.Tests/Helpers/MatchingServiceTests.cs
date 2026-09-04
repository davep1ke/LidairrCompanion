using LidarrCompanion.Helpers;

namespace LidarrCompanion.Core.Tests.Helpers
{
    public class MatchingServiceTests
    {
        [Fact]
        public void CleanString_RemovesFillerWordsAndPunctuation()
        {
            Assert.Equal("artist friend", MatchingService.CleanString("Artist and Friend"));
        }

        [Fact]
        public void CleanString_RemovesFeatVariants()
        {
            Assert.Equal("song title someone", MatchingService.CleanString("Song Title feat. Someone"));
        }

        [Fact]
        public void CleanString_NullOrWhitespace_ReturnsEmpty()
        {
            Assert.Equal(string.Empty, MatchingService.CleanString(""));
        }

        [Fact]
        public void MinimalString_RemovesVowelsAndCollapsesDoubleLettersAndTrailingS()
        {
            // hello -> h,(e skip),l,(l dup skip),(o skip) => "hl"
            // world -> w,(o skip),r,l,d => "wrld"
            Assert.Equal("hl wrld", MatchingService.MinimalString("Hello World"));
        }

        [Fact]
        public void MinimalString_DropsTrailingS()
        {
            // cats -> c,(a skip),t,s -> "cts" -> trailing s dropped -> "ct"
            Assert.Equal("ct", MatchingService.MinimalString("cats"));
        }

        [Theory]
        [InlineData("3. Song Name", 3)]
        [InlineData("12 - Another Song", 12)]
        [InlineData("Song Name", int.MaxValue)]
        [InlineData("", int.MaxValue)]
        public void ParseTrackNumber_ExtractsLeadingNumberOrMaxValue(string input, int expected)
        {
            Assert.Equal(expected, MatchingService.ParseTrackNumber(input));
        }

        [Theory]
        [InlineData("3. Song Name", "Song Name")]
        [InlineData("03 - Song Name", "Song Name")]
        [InlineData("Song Name", "Song Name")]
        public void StripTrackNumberPrefix_RemovesLeadingTrackNumber(string input, string expected)
        {
            Assert.Equal(expected, MatchingService.StripTrackNumberPrefix(input));
        }

        [Fact]
        public void WordMatchScore_ScoresByFractionOfMatchedWords()
        {
            // "hello" matches, "world" doesn't -> 1 of max(2,2) words -> 0.5 * 10 = 5.0
            Assert.Equal(5.0, MatchingService.WordMatchScore("hello world", "hello there", 10));
        }

        [Fact]
        public void WordMatchScore_IdenticalStrings_ReturnsMaxPoints()
        {
            Assert.Equal(10.0, MatchingService.WordMatchScore("hello world", "hello world", 10));
        }

        [Fact]
        public void WordMatchScore_EmptyInput_ReturnsZero()
        {
            Assert.Equal(0.0, MatchingService.WordMatchScore("", "hello", 10));
        }

        [Fact]
        public void GetManualMatchCandidates_MatchesArtistByWordOverlap()
        {
            var artists = new List<LidarrArtist>
            {
                new LidarrArtist { ArtistName = "The Beatles" },
                new LidarrArtist { ArtistName = "Pink Floyd" }
            };

            var candidates = MatchingService.GetManualMatchCandidates("The Beatles - Abbey Road", artists);

            Assert.Single(candidates);
            Assert.Equal("The Beatles", candidates[0].ArtistName);
        }

        [Fact]
        public void GetManualMatchCandidates_NoMatch_ReturnsEmpty()
        {
            var artists = new List<LidarrArtist> { new LidarrArtist { ArtistName = "Pink Floyd" } };

            var candidates = MatchingService.GetManualMatchCandidates("Some Unrelated Release Name", artists);

            Assert.Empty(candidates);
        }

        [Fact]
        public void AutoMatchReleasesToArtists_ExactFolderNameMatch_SetsExactMatch()
        {
            var queueRecords = new List<LidarrQueueRecord>
            {
                new LidarrQueueRecord { Id = 1, Title = "t", DownloadId = "d", OutputPath = "/downloads/The Beatles" }
            };
            var artists = new List<LidarrArtist> { new LidarrArtist { ArtistName = "The Beatles" } };

            MatchingService.AutoMatchReleasesToArtists(queueRecords, artists, importPath: "/downloads");

            Assert.Equal(ReleaseMatchType.Exact, queueRecords[0].Match);
            Assert.Equal("The Beatles", queueRecords[0].MatchedArtist);
        }

        [Fact]
        public void AutoMatchReleasesToArtists_ArtistDashAlbumFolder_SetsArtistFirstMatch()
        {
            var queueRecords = new List<LidarrQueueRecord>
            {
                new LidarrQueueRecord { Id = 1, Title = "t", DownloadId = "d", OutputPath = "/downloads/The Beatles - Abbey Road" }
            };
            var artists = new List<LidarrArtist> { new LidarrArtist { ArtistName = "The Beatles" } };

            MatchingService.AutoMatchReleasesToArtists(queueRecords, artists, importPath: "/downloads");

            Assert.Equal(ReleaseMatchType.ArtistFirst, queueRecords[0].Match);
            Assert.Equal("The Beatles", queueRecords[0].MatchedArtist);
        }

        [Fact]
        public void AutoMatchReleasesToArtists_NoMatch_LeavesMatchTypeNone()
        {
            var queueRecords = new List<LidarrQueueRecord>
            {
                new LidarrQueueRecord { Id = 1, Title = "t", DownloadId = "d", OutputPath = "/downloads/Completely Unknown Release" }
            };
            var artists = new List<LidarrArtist> { new LidarrArtist { ArtistName = "The Beatles" } };

            MatchingService.AutoMatchReleasesToArtists(queueRecords, artists, importPath: "/downloads");

            Assert.Equal(ReleaseMatchType.None, queueRecords[0].Match);
            Assert.Equal(string.Empty, queueRecords[0].MatchedArtist);
        }

        [Fact]
        public void AutoMatchReleasesToArtists_NoArtists_Throws()
        {
            Assert.Throws<ArgumentException>(() =>
                MatchingService.AutoMatchReleasesToArtists(new List<LidarrQueueRecord>(), new List<LidarrArtist>(), "/downloads"));
        }
    }
}
