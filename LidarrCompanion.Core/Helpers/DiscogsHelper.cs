using LidarrCompanion.Models;
using System.Net.Http;
using System.Text.Json;

namespace LidarrCompanion.Helpers
{
    // A single Discogs search result. ThumbUrl/CoverImageUrl are both null unless the request was
    // authenticated - Discogs deliberately omits image URLs from unauthenticated search responses
    // (confirmed directly against the live API), regardless of the per-minute rate limit.
    public record DiscogsRelease(long Id, string Title, string? Year, string? ThumbUrl, string? CoverImageUrl);

    // Replaces MusicBrainz/Cover Art Archive as the cover-art gate's primary lookup. Discogs'
    // catalog leans heavily into vinyl/promo/remix releases that MusicBrainz's own community
    // catalog is thinner on, and its search results embed image URLs directly - no separate
    // per-candidate archive fetch needed the way MusicBrainz+Cover Art Archive required.
    // MusicBrainzHelper itself is untouched and still used for artist matching elsewhere
    // (ManualMatchDialog) - only the cover-art gate's own search moved to Discogs.
    public class DiscogsHelper
    {
        private static readonly HttpClient _httpClient = new HttpClient();
        private const string BaseUrl = "https://api.discogs.com";

        static DiscogsHelper()
        {
            // Discogs requires an identifying User-Agent on every request.
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "LidarrCompanion/1.0 (+https://github.com/davep1ke/LidairrCompanion)");
        }

        // Structured artist+album search (mirrors the old MusicBrainz automatic tier), for the
        // "Search Discogs" button. Deliberately returns candidates rather than silently
        // downloading/staging the first one - auto-staging a plausible-but-wrong match on a
        // structured-but-imprecise search was a real complaint about the old MusicBrainz flow.
        public Task<List<DiscogsRelease>> SearchAsync(string token, string? artist, string? album, int limit = 10) =>
            SearchReleasesAsync(token, artist, album, limit);

        // Artist-only browse (mirrors the old MusicBrainz "browse by artist" tier) - for when a
        // combined artist+album search finds nothing, letting the user pick visually from
        // everything Discogs has for the artist instead of the search just giving up.
        public Task<List<DiscogsRelease>> BrowseByArtistAsync(string token, string artist, int limit = 25) =>
            SearchReleasesAsync(token, artist, album: null, limit);

        private async Task<List<DiscogsRelease>> SearchReleasesAsync(string token, string? artist, string? album, int limit)
        {
            var results = new List<DiscogsRelease>();
            if (string.IsNullOrWhiteSpace(token)) return results;
            if (string.IsNullOrWhiteSpace(artist) && string.IsNullOrWhiteSpace(album)) return results;

            try
            {
                var url = BuildSearchUrl(artist, album, limit);

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("Authorization", $"Discogs token={token}");

                Logger.Log($"Searching Discogs: artist='{artist}', album='{album}'", LogSeverity.Verbose, new { Artist = artist, Album = album, Url = url });
                var response = await _httpClient.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    Logger.Log($"Discogs search returned {(int)response.StatusCode}", LogSeverity.Low, new { StatusCode = (int)response.StatusCode });
                    return results;
                }

                var content = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(content);
                if (!doc.RootElement.TryGetProperty("results", out var items) || items.ValueKind != JsonValueKind.Array)
                    return results;

                foreach (var item in items.EnumerateArray())
                {
                    var id = item.TryGetProperty("id", out var idProp) ? idProp.GetInt64() : 0;
                    var title = item.TryGetProperty("title", out var titleProp) ? titleProp.GetString() : null;
                    var year = item.TryGetProperty("year", out var yearProp) ? yearProp.GetString() : null;
                    var thumb = item.TryGetProperty("thumb", out var thumbProp) ? thumbProp.GetString() : null;
                    var coverImage = item.TryGetProperty("cover_image", out var coverProp) ? coverProp.GetString() : null;

                    results.Add(new DiscogsRelease(
                        id,
                        string.IsNullOrWhiteSpace(title) ? "(untitled)" : title,
                        year,
                        string.IsNullOrWhiteSpace(thumb) ? null : thumb,
                        string.IsNullOrWhiteSpace(coverImage) ? null : coverImage));
                }

                Logger.Log($"Discogs search found {results.Count} release(s)", LogSeverity.Verbose, new { Count = results.Count });
            }
            catch (Exception ex)
            {
                Logger.Log($"Discogs search failed: {ex.Message}", LogSeverity.Low, new { Artist = artist, Album = album, Error = ex.Message });
            }

            return results;
        }

        // Query-building extracted so the "which field goes where" choice is directly
        // regression-tested, the same way the MusicBrainz query builder is.
        internal static string BuildSearchUrl(string? artist, string? album, int limit)
        {
            var parts = new List<string> { "type=release", $"per_page={limit}" };
            if (!string.IsNullOrWhiteSpace(artist)) parts.Add($"artist={Uri.EscapeDataString(artist)}");
            if (!string.IsNullOrWhiteSpace(album)) parts.Add($"release_title={Uri.EscapeDataString(album)}");
            return $"{BaseUrl}/database/search?{string.Join("&", parts)}";
        }

        public async Task<byte[]?> DownloadImageAsync(string imageUrl)
        {
            return await _httpClient.GetByteArrayAsync(imageUrl);
        }
    }
}
