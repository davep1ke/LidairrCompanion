using LidarrCompanion.Helpers;

namespace LidarrCompanion.Core.Tests.Helpers
{
    public class SerpApiHelperTests
    {
        [Theory]
        [InlineData("http://example.com/image.jpg", true)]
        [InlineData("https://example.com/image.jpg", true)]
        // "original" is sometimes a non-fetchable pseudo-URL Google Images hands back - a real
        // quirk observed live, not a hypothetical - and must be filtered out rather than passed
        // through as a source, or the resulting <img>/download request just fails.
        [InlineData("x-raw-image://abcdef123456", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        [InlineData("not a url at all", false)]
        public void IsFetchableUrl_OnlyAcceptsAbsoluteHttpOrHttps(string? url, bool expected)
        {
            Assert.Equal(expected, SerpApiHelper.IsFetchableUrl(url));
        }
    }
}
