using System.Net.Http;
using System.Text.Json;

namespace LidarrCompanion.Helpers
{
    public class SerpApiImageResult
    {
        public string ThumbnailUrl { get; set; } = string.Empty;
        public string FullUrl { get; set; } = string.Empty;
        public string? Title { get; set; }
        // SerpApi provides these directly on the search response (original_width/original_height) -
        // no need to load the image just to know its size.
        public int? Width { get; set; }
        public int? Height { get; set; }
        // "link" is the webpage the image was found on (not the image URL itself) and "source" is
        // that page's site name (e.g. "Wikipedia") - the closest thing Google Images results have
        // to a description, and a real, clickable attribution link.
        public string? SourceUrl { get; set; }
        public string? SourceName { get; set; }
    }

    // Manual-only fallback image search, used solely when the free MusicBrainz/Cover Art Archive
    // lookup finds nothing for a file. SerpApi's free tier covers up to 250 searches/month, which
    // this stays well within since it's triggered by an explicit button click, not automatically.
    public class SerpApiHelper
    {
        private static readonly HttpClient _httpClient = new HttpClient();

        // limit default raised from 16 to 60 - SerpApi already returns up to 100 results per
        // search call (confirmed directly against the live API) at no extra cost, so the old
        // default was throwing away results the same call already paid for, not a real API
        // constraint.
        public async Task<List<SerpApiImageResult>> SearchImagesAsync(string apiKey, string query, int limit = 60)
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
                    var width = img.TryGetProperty("original_width", out var w) && w.ValueKind == JsonValueKind.Number ? w.GetInt32() : (int?)null;
                    var height = img.TryGetProperty("original_height", out var h) && h.ValueKind == JsonValueKind.Number ? h.GetInt32() : (int?)null;
                    var sourceUrl = img.TryGetProperty("link", out var lk) ? lk.GetString() : null;
                    var sourceName = img.TryGetProperty("source", out var src) ? src.GetString() : null;

                    var (thumb, full) = ChooseImageUrls(original, thumbnail);
                    if (string.IsNullOrWhiteSpace(full)) continue;

                    // Google Images results routinely include animated GIFs (confirmed live - a
                    // "reaction gif"-style query returned 77/100 results as .gif) which are never
                    // right for cover art. No dedicated "is animated" field exists in the API
                    // response, so this is a plain extension check on whichever URL was chosen
                    // above - not foolproof (a GIF served without that extension slips through)
                    // but covers the overwhelming majority of real cases.
                    if (IsGifUrl(full)) continue;

                    results.Add(new SerpApiImageResult
                    {
                        ThumbnailUrl = thumb!,
                        FullUrl = full,
                        Title = title,
                        Width = width,
                        Height = height,
                        SourceUrl = IsFetchableUrl(sourceUrl) ? sourceUrl : null,
                        SourceName = sourceName
                    });
                }
            }

            return results;
        }

        public async Task<byte[]> DownloadImageAsync(string imageUrl)
        {
            return await _httpClient.GetByteArrayAsync(imageUrl);
        }

        // The results grid shows Google's own small thumbnail; the full-size original is only used
        // for the single selected-image preview and the eventual download. Grid thumbnails used to
        // be the originals too (sharper), but a grid of dozens of multi-megapixel originals made
        // the page slow to fill in for no benefit at thumbnail size.
        //
        // "original" can be a non-fetchable "x-raw-image://..." pseudo-URL for some results (a
        // known quirk of Google's own image data, not a SerpApi bug), so each is only trusted when
        // it's an actual http(s) URL, and either one stands in for the other when it's missing.
        internal static (string? Thumbnail, string? Full) ChooseImageUrls(string? original, string? thumbnail)
        {
            var fetchableOriginal = IsFetchableUrl(original) ? original : null;
            var fetchableThumbnail = IsFetchableUrl(thumbnail) ? thumbnail : null;
            return (fetchableThumbnail ?? fetchableOriginal, fetchableOriginal ?? fetchableThumbnail);
        }

        internal static bool IsFetchableUrl(string? url) =>
            !string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out var u) &&
            (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

        internal static bool IsGifUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var u))
                return false;
            return u.AbsolutePath.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);
        }
    }
}
