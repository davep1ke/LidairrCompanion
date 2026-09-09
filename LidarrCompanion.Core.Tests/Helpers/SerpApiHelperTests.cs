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

        [Theory]
        [InlineData("https://media.tenor.com/B9_pYXrrTGUAAAAM/yes.gif", true)]
        [InlineData("https://media4.giphy.com/media/abc/200.gif", true)]
        [InlineData("HTTPS://EXAMPLE.COM/IMAGE.GIF", true)]
        [InlineData("https://example.com/image.gif?width=500", true)]
        [InlineData("https://example.com/image.jpg", false)]
        [InlineData("https://example.com/gif-of-the-day.html", false)]
        [InlineData(null, false)]
        [InlineData("", false)]
        public void IsGifUrl_DetectsGifExtensionIgnoringCaseAndQueryString(string? url, bool expected)
        {
            Assert.Equal(expected, SerpApiHelper.IsGifUrl(url));
        }
    }
}
