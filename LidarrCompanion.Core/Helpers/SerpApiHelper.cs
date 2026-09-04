using System.Net.Http;
using System.Text.Json;

namespace LidarrCompanion.Helpers
{
    public class SerpApiImageResult
    {
        public string ThumbnailUrl { get; set; } = string.Empty;
        public string FullUrl { get; set; } = string.Empty;
        public string? Title { get; set; }
    }

    // Manual-only fallback image search, used solely when the free MusicBrainz/Cover Art Archive
    // lookup finds nothing for a file. SerpApi's free tier covers up to 250 searches/month, which
    // this stays well within since it's triggered by an explicit button click, not automatically.
    public class SerpApiHelper
    {
        private static readonly HttpClient _httpClient = new HttpClient();

        public async Task<List<SerpApiImageResult>> SearchImagesAsync(string apiKey, string query, int limit = 16)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("SerpApi key is not configured.");
            if (string.IsNullOrWhiteSpace(query))
                return new List<SerpApiImageResult>();

            var url = $"https://serpapi.com/search.json?engine=google_images&q={Uri.EscapeDataString(query)}&api_key={Uri.EscapeDataString(apiKey)}";

            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync();

            var results = new List<SerpApiImageResult>();
            using var doc = JsonDocument.Parse(content);

            if (doc.RootElement.TryGetProperty("images_results", out var images) && images.ValueKind == JsonValueKind.Array)
            {
                foreach (var img in images.EnumerateArray())
                {
                    if (results.Count >= limit) break;

                    var thumbnail = img.TryGetProperty("thumbnail", out var t) ? t.GetString() : null;
                    var original = img.TryGetProperty("original", out var o) ? o.GetString() : null;
                    var title = img.TryGetProperty("title", out var ti) ? ti.GetString() : null;

                    // "original" can be a non-fetchable "x-raw-image://..." pseudo-URL for some
                    // results (a known quirk of Google's own image data, not a SerpApi bug) -
                    // only trust it as a real, downloadable source when it's an actual http(s)
                    // URL. Preferring it over "thumbnail" for display too (not just download) is
                    // what makes the grid's previews sharp instead of Google's ~100px thumbnails;
                    // CSS still constrains the rendered size, so this doesn't blow the layout up.
                    var fetchableOriginal = IsFetchableUrl(original) ? original : null;
                    var fetchableThumbnail = IsFetchableUrl(thumbnail) ? thumbnail : null;
                    var full = fetchableOriginal ?? fetchableThumbnail;
                    if (string.IsNullOrWhiteSpace(full)) continue;

                    results.Add(new SerpApiImageResult
                    {
                        ThumbnailUrl = full,
                        FullUrl = full,
                        Title = title
                    });
                }
            }

            return results;
        }

        public async Task<byte[]> DownloadImageAsync(string imageUrl)
        {
            return await _httpClient.GetByteArrayAsync(imageUrl);
        }

        internal static bool IsFetchableUrl(string? url) =>
            !string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out var u) &&
            (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);
    }
}
