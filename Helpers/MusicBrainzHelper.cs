using System.Net.Http;
using System.Text.Json;

namespace LidarrCompanion.Helpers
{
    public class MusicBrainzHelper
    {
        private static readonly HttpClient _httpClient = new HttpClient();
        private const string BaseUrl = "https://musicbrainz.org/ws/2";

        static MusicBrainzHelper()
        {
            // MusicBrainz requires a User-Agent header
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "LidarrCompanion/1.0 (https://github.com/davep1ke/LidairrCompanion)");
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