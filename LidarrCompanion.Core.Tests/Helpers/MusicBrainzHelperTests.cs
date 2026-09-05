using LidarrCompanion.Helpers;

namespace LidarrCompanion.Core.Tests.Helpers
{
    public class MusicBrainzHelperTests
    {
        [Fact]
        public void BuildReleaseGroupQuery_ArtistClause_IsParenthesizedNotQuoted()
        {
            // Regression test for a real bug: an exact-phrase-quoted artist clause never matches
            // MusicBrainz's clean artist field ("David Guetta") against messy real file tags like
            // "David Guetta Feat. JD Davis" - confirmed via direct MusicBrainz API testing that
            // the quoted form returns 0 results while the parenthesized form matches correctly.
            var query = MusicBrainzHelper.BuildReleaseGroupQuery("David Guetta Feat. JD Davis", album: null);

            Assert.Equal("artist:(David Guetta Feat. JD Davis)", query);
            Assert.DoesNotContain("artist:\"", query);
        }

        [Fact]
        public void BuildReleaseGroupQuery_AlbumClause_IsParenthesizedNotQuoted()
        {
            // Regression test for a second real bug found the same way as the artist one above:
            // an exact-phrase-quoted album clause returns 0 results for a file tagged with the
            // British spelling "Your Favourite Toy" against MusicBrainz's catalogued "Your
            // Favorite Toy" (Foo Fighters) - confirmed live against the API. Loosening it the same
            // way as the artist clause fixes it.
            var query = MusicBrainzHelper.BuildReleaseGroupQuery(artist: null, album: "Guetta Blaster");

            Assert.Equal("releasegroup:(Guetta Blaster)", query);
            Assert.DoesNotContain("releasegroup:\"", query);
        }

        [Fact]
        public void BuildReleaseGroupQuery_BothPresent_JoinsWithAnd()
        {
            var query = MusicBrainzHelper.BuildReleaseGroupQuery("David Guetta", "Guetta Blaster");

            Assert.Equal("releasegroup:(Guetta Blaster) AND artist:(David Guetta)", query);
        }

        [Fact]
        public void BuildReleaseGroupQuery_BothMissing_ReturnsEmpty()
        {
            var query = MusicBrainzHelper.BuildReleaseGroupQuery(null, null);

            Assert.Equal(string.Empty, query);
        }

        [Theory]
        [InlineData("Bovie & Rox Vs Rivaro")]
        [InlineData("Artist (Extended Mix)")]
        [InlineData("Artist \"Nickname\" Real Name")]
        public void SanitizeForQuery_StripsCharactersThatWouldBreakTheLuceneClause(string input)
        {
            var sanitized = MusicBrainzHelper.SanitizeForQuery(input);

            Assert.DoesNotContain("\"", sanitized);
            Assert.DoesNotContain("(", sanitized);
            Assert.DoesNotContain(")", sanitized);
        }

        [Fact]
        public void SanitizeForQuery_TrimsSurroundingWhitespaceLeftBySanitization()
        {
            Assert.Equal("Artist", MusicBrainzHelper.SanitizeForQuery("(Artist)"));
        }
    }
}
