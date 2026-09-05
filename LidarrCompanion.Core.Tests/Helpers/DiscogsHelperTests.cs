using LidarrCompanion.Helpers;

namespace LidarrCompanion.Core.Tests.Helpers
{
    public class DiscogsHelperTests
    {
        [Fact]
        public void BuildSearchUrl_ArtistOnly_OmitsReleaseTitleParam()
        {
            var url = DiscogsHelper.BuildSearchUrl("Foo Fighters", album: null, limit: 10);

            Assert.Contains("artist=Foo%20Fighters", url);
            Assert.DoesNotContain("release_title=", url);
        }

        [Fact]
        public void BuildSearchUrl_AlbumOnly_OmitsArtistParam()
        {
            var url = DiscogsHelper.BuildSearchUrl(artist: null, "Your Favorite Toy", limit: 10);

            Assert.Contains("release_title=Your%20Favorite%20Toy", url);
            Assert.DoesNotContain("artist=", url);
        }

        [Fact]
        public void BuildSearchUrl_BothPresent_IncludesBothAsStructuredFields()
        {
            var url = DiscogsHelper.BuildSearchUrl("Foo Fighters", "Your Favorite Toy", limit: 5);

            Assert.Contains("artist=Foo%20Fighters", url);
            Assert.Contains("release_title=Your%20Favorite%20Toy", url);
            Assert.Contains("type=release", url);
            Assert.Contains("per_page=5", url);
        }

        [Fact]
        public void BuildSearchUrl_AlwaysScopesToReleases()
        {
            var url = DiscogsHelper.BuildSearchUrl("Any Artist", null, 1);

            Assert.Contains("type=release", url);
        }
    }
}
