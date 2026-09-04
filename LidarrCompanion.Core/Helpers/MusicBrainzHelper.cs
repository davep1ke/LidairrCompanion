using LidarrCompanion.Models;
using System.Net.Http;
using System.Text.Json;
using System.Threading;

namespace LidarrCompanion.Helpers
{
    public class MusicBrainzHelper
    {
        private static readonly HttpClient _httpClient = new HttpClient();
        private const string BaseUrl = "https://musicbrainz.org/ws/2";
        private const string CoverArtArchiveBaseUrl = "https://coverartarchive.org";

        // MusicBrainz's unauthenticated rate limit is ~1 request/second. Both the search and the
        // Cover Art Archive fetch that can follow it are throttled through this same gate, since
        // the automatic cover-art lookup runs unattended per queued file rather than on an
        // explicit user click.
        private static readonly SemaphoreSlim _throttleLock = new(1, 1);
        private static DateTime _lastRequestUtc = DateTime.MinValue;
        private static readonly TimeSpan MinRequestInterval = TimeSpan.FromMilliseconds(1100);

        static MusicBrainzHelper()
        {
            // MusicBrainz requires a User-Agent header
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "LidarrCompanion/1.0 (https://github.com/davep1ke/LidairrCompanion)");
        }

        // Strips characters that have special meaning in MusicBrainz's Lucene query syntax
        // (quotes would close the clause early; parens would break the artist grouping) - not a
        // full Lucene escaper, just enough that arbitrary file-tag text can't corrupt the query.
        internal static string SanitizeForQuery(string s) =>
            s.Replace("\"", " ").Replace("(", " ").Replace(")", " ").Trim();

        // Album stays an exact quoted phrase (titles are usually clean and specific enough that
        // phrase-matching helps). Artist is deliberately NOT phrase-quoted - real file tags
        // routinely read "David Guetta Feat. JD Davis" or "Bovie & Rox Vs Rivaro", and
        // MusicBrainz's own artist field only ever holds the single credited name ("David
        // Guetta"); an exact-phrase match against the messy tag string just never matches
        // anything. A parenthesised, unquoted clause instead lets Lucene treat it as a set of
        // terms contributing to relevance rather than a literal phrase, so it still matches when
        // the tag has extra "feat."/"&"/"vs." text. Extracted so this exact quoting choice - the
        // root cause of a real "search finds nothing" bug - is directly regression-tested.
        internal static string BuildReleaseGroupQuery(string? artist, string? album)
        {
            var clauses = new List<string>();
            if (!string.IsNullOrWhiteSpace(album)) clauses.Add($"releasegroup:\"{SanitizeForQuery(album)}\"");
            if (!string.IsNullOrWhiteSpace(artist)) clauses.Add($"artist:({SanitizeForQuery(artist)})");
            return string.Join(" AND ", clauses);
        }

        private static async Task ThrottleAsync()
        {
            await _throttleLock.WaitAsync();
            try
            {
                var wait = MinRequestInterval - (DateTime.UtcNow - _lastRequestUtc);
                if (wait > TimeSpan.Zero)
                    await Task.Delay(wait);
                _lastRequestUtc = DateTime.UtcNow;
            }
            finally
            {
                _throttleLock.Release();
            }
        }

        // Best-effort automatic cover art lookup: search MusicBrainz release-groups by
        // artist/album text (no pre-resolved MBID needed), then fetch the front image for the
        // first candidate that the Cover Art Archive actually has one for. Returns null - never
        // throws - on any miss or failure, since this runs unattended as the first tier of the
        // cover-art gate; the caller falls back to a manual SerpApi search.
        public async Task<byte[]?> TryGetCoverArtAsync(string? artist, string? album)
        {
            if (string.IsNullOrWhiteSpace(artist) && string.IsNullOrWhiteSpace(album))
                return null;

            try
            {
                var luceneQuery = BuildReleaseGroupQuery(artist, album);
                var query = Uri.EscapeDataString(luceneQuery);
                var url = $"{BaseUrl}/release-group/?query={query}&fmt=json&limit=5";

                await ThrottleAsync();
                Logger.Log($"Searching MusicBrainz for cover art: artist='{artist}', album='{album}'", LogSeverity.Verbose, new { Artist = artist, Album = album, Url = url });
                var response = await _httpClient.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                {
                    Logger.Log($"MusicBrainz search returned {(int)response.StatusCode}", LogSeverity.Low, new { StatusCode = (int)response.StatusCode });
                    return null;
                }

                var content = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(content);

                if (!doc.RootElement.TryGetProperty("release-groups", out var groups) || groups.ValueKind != JsonValueKind.Array)
                {
                    Logger.Log("MusicBrainz search returned no release-groups field", LogSeverity.Verbose);
                    return null;
                }

                var candidateCount = groups.GetArrayLength();
                Logger.Log($"MusicBrainz search found {candidateCount} release-group candidate(s)", LogSeverity.Verbose, new { Count = candidateCount });

                foreach (var group in groups.EnumerateArray())
                {
                    if (!group.TryGetProperty("id", out var idProp)) continue;
                    var mbid = idProp.GetString();
                    if (string.IsNullOrWhiteSpace(mbid)) continue;

                    var art = await TryFetchCoverArtArchiveAsync(mbid);
                    if (art is not null)
                    {
                        Logger.Log($"Found cover art via MusicBrainz release-group {mbid}", LogSeverity.Low, new { Mbid = mbid, Bytes = art.Length });
                        return art;
                    }
                }

                Logger.Log("No candidate release-group had cover art in the Cover Art Archive", LogSeverity.Verbose);
            }
            catch (Exception ex)
            {
                // Best-effort - swallow and let the caller fall back to manual search, but log it
                // so a persistent failure (as opposed to a genuine miss) is actually visible.
                Logger.Log($"MusicBrainz cover art lookup failed: {ex.Message}", LogSeverity.Low, new { Artist = artist, Album = album, Error = ex.Message });
            }

            return null;
        }

        private async Task<byte[]?> TryFetchCoverArtArchiveAsync(string releaseGroupMbid)
        {
            try
            {
                await ThrottleAsync();
                var url = $"{CoverArtArchiveBaseUrl}/release-group/{releaseGroupMbid}/front-500";
                var response = await _httpClient.GetAsync(url);
                if (!response.IsSuccessStatusCode) return null;
                return await response.Content.ReadAsByteArrayAsync();
            }
            catch (Exception ex)
            {
                Logger.Log($"Cover Art Archive fetch failed for {releaseGroupMbid}: {ex.Message}", LogSeverity.Verbose, new { Mbid = releaseGroupMbid, Error = ex.Message });
                return null;
            }
        }

        public async Task<List<string>> SearchArtistsRawAsync(string searchTerm)
        {
            var results = new List<string>();

            try
            {
                var encodedTerm = Uri.EscapeDataString(searchTerm);
                var url = $"{BaseUrl}/artist/?query={encodedTerm}&fmt=json&limit=25";

                var response = await _httpClient.GetAsync(url);
                response.EnsureSuccessStatusCode();

                var content = await response.Content.ReadAsStringAsync();

                using var doc = JsonDocument.Parse(content);

                if (doc.RootElement.TryGetProperty("artists", out var artistsArray) &&
                    artistsArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var artist in artistsArray.EnumerateArray())
                    {
                        results.Add(artist.GetRawText());
                    }
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"MusicBrainz search failed: {ex.Message}", ex);
            }

            return results;
        }
    }
}
