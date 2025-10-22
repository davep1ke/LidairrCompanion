using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LidairrCompanion.Models;

namespace LidairrCompanion.Helpers
{
    public class LidarrHelper
    {
        private readonly string _baseUrl;
        private readonly string _apiKey;
        private readonly HttpClient _client;

        public LidarrHelper()
        {
            _baseUrl = AppSettings.GetValue(SettingKey.LidarrURL)?.TrimEnd('/');
            _apiKey = AppSettings.GetValue(SettingKey.LidarrAPIKey);

            _client = new HttpClient();
            _client.Timeout = TimeSpan.FromSeconds(20);
        }

        public async Task<IList<LidarrQueueRecord>> GetBlockedCompletedQueueAsync(int page = 1, int pageSize = 50)
        {
            if (string.IsNullOrWhiteSpace(_baseUrl) || string.IsNullOrWhiteSpace(_apiKey))
                throw new InvalidOperationException("Lidarr URL or API Key is not set.");

            var url = $"{_baseUrl}/api/v1/queue?page={page}&pageSize={pageSize}" +
                      $"&includeUnknownArtistItems=true&includeArtist=true&includeAlbum=true&apikey={_apiKey}";

            var response = await _client.GetAsync(url);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();

            var results = new List<LidarrQueueRecord>();

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("records", out var records))
            {
                foreach (var record in records.EnumerateArray())
                {
                    var status = record.GetProperty("status").GetString();
                    var trackedDownloadState = record.GetProperty("trackedDownloadState").GetString();

                    if (status == "completed" && trackedDownloadState == "importBlocked")
                    {
                        results.Add(new LidarrQueueRecord
                        {
                            Title = record.GetProperty("title").GetString(),
                            OutputPath = record.GetProperty("outputPath").GetString(),
                            Id = record.GetProperty("id").GetInt32(),
                            DownloadId = record.GetProperty("downloadId").GetString()
                        });
                    }
                }
            }

            return results;
        }

        public async Task<IList<LidarrManualImportFile>> GetFilesInReleaseAsync(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(_baseUrl) || string.IsNullOrWhiteSpace(_apiKey))
                throw new InvalidOperationException("Lidarr URL or API Key is not set.");

            var url = $"{_baseUrl}/api/v1/manualimport?folder={Uri.EscapeDataString(folderPath)}&apikey={_apiKey}";

            var response = await _client.GetAsync(url);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();

            var results = new List<LidarrManualImportFile>();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    results.Add(new LidarrManualImportFile
                    {
                        Path = item.GetProperty("path").GetString(),
                        Name = item.GetProperty("name").GetString(),
                        Id = item.GetProperty("id").GetInt32()
                    });
                }
            }
            return results;
        }

        // Use X-Api-Key header and stream parsing; optional CancellationToken
        public async Task<IList<LidarrArtist>> GetAllArtistsAsync(CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(_baseUrl) || string.IsNullOrWhiteSpace(_apiKey))
                throw new InvalidOperationException("Lidarr URL or API Key is not set.");

            var uriBuilder = new UriBuilder(_baseUrl);
            var basePath = uriBuilder.Path?.TrimEnd('/') ?? string.Empty;
            uriBuilder.Path = $"{basePath}/api/v1/artist".TrimStart('/');

            using var request = new HttpRequestMessage(HttpMethod.Get, uriBuilder.Uri);
            request.Headers.Add("X-Api-Key", _apiKey);

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linkedCts.CancelAfter(_client.Timeout);

            HttpResponseMessage response;
            try
            {
                response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Request to Lidarr timed out.", ex);
            }

            response.EnsureSuccessStatusCode();

            var results = new List<LidarrArtist>();

            await using var stream = await response.Content.ReadAsStreamAsync(linkedCts.Token).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: linkedCts.Token).ConfigureAwait(false);

            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    results.Add(new LidarrArtist
                    {
                        ArtistName = item.GetProperty("artistName").GetString(),
                        Id = item.GetProperty("id").GetInt32()
                    });
                }
            }

            return results;
        }

        // Get albums for an artist (includeAllArtistAlbums=true)
        public async Task<IList<LidarrAlbum>> GetAlbumsByArtistAsync(int artistId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(_baseUrl) || string.IsNullOrWhiteSpace(_apiKey))
                throw new InvalidOperationException("Lidarr URL or API Key is not set.");

            var uriBuilder = new UriBuilder(_baseUrl);
            var basePath = uriBuilder.Path?.TrimEnd('/') ?? string.Empty;
            uriBuilder.Path = $"{basePath}/api/v1/album".TrimStart('/');

            var query = $"artistId={artistId}&includeAllArtistAlbums=true";
            uriBuilder.Query = query;

            using var request = new HttpRequestMessage(HttpMethod.Get, uriBuilder.Uri);
            request.Headers.Add("X-Api-Key", _apiKey);

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linkedCts.CancelAfter(_client.Timeout);

            HttpResponseMessage response;
            try
            {
                response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Request to Lidarr timed out.", ex);
            }

            response.EnsureSuccessStatusCode();

            var results = new List<LidarrAlbum>();
            await using var stream = await response.Content.ReadAsStreamAsync(linkedCts.Token).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: linkedCts.Token).ConfigureAwait(false);

            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    var album = new LidarrAlbum
                    {
                        Title = item.TryGetProperty("title", out var t) ? t.GetString() ?? string.Empty : string.Empty,
                        Id = item.TryGetProperty("id", out var id) ? id.GetInt32() : 0
                    };

                    if (item.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var img in images.EnumerateArray())
                        {
                            if (img.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String)
                                album.ImageUrls.Add(url.GetString() ?? string.Empty);
                        }
                    }

                    if (item.TryGetProperty("releases", out var releases) && releases.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var r in releases.EnumerateArray())
                        {
                            var rel = new LidarrAlbumRelease
                            {
                                Id = r.TryGetProperty("id", out var rid) && rid.ValueKind == JsonValueKind.Number ? rid.GetInt32() : 0,
                                Title = r.TryGetProperty("title", out var rtitle) ? rtitle.GetString() ?? string.Empty : string.Empty,
                                Format = r.TryGetProperty("format", out var fmt) ? fmt.GetString() ?? string.Empty : string.Empty,
                                PublishDate = r.TryGetProperty("publishDate", out var pd) ? pd.GetString() ?? string.Empty : string.Empty
                            };

                            if (r.TryGetProperty("country", out var country) && country.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var c in country.EnumerateArray())
                                {
                                    if (c.ValueKind == JsonValueKind.String)
                                    {
                                        rel.Country = c.GetString() ?? string.Empty;
                                        break;
                                    }
                                }
                            }

                            if (r.TryGetProperty("label", out var label) && label.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var l in label.EnumerateArray())
                                {
                                    if (l.ValueKind == JsonValueKind.String)
                                    {
                                        rel.Label = l.GetString() ?? string.Empty;
                                        break;
                                    }
                                }
                            }

                            album.Releases.Add(rel);
                        }
                    }

                    results.Add(album);
                }
                results.Sort((a, b) => string.Compare(a.Title , b.Title, StringComparison.OrdinalIgnoreCase));
            }

            return results;
        }

        // Get tracks for a given albumReleaseId
        public async Task<IList<LidarrTrack>> GetTracksByReleaseAsync(int albumReleaseId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(_baseUrl) || string.IsNullOrWhiteSpace(_apiKey))
                throw new InvalidOperationException("Lidarr URL or API Key is not set.");

            var uriBuilder = new UriBuilder(_baseUrl);
            var basePath = uriBuilder.Path?.TrimEnd('/') ?? string.Empty;
            uriBuilder.Path = $"{basePath}/api/v1/track".TrimStart('/');

            var query = $"albumReleaseId={albumReleaseId}";
            uriBuilder.Query = query;

            using var request = new HttpRequestMessage(HttpMethod.Get, uriBuilder.Uri);
            request.Headers.Add("X-Api-Key", _apiKey);

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linkedCts.CancelAfter(_client.Timeout);

            HttpResponseMessage response;
            try
            {
                response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Request to Lidarr timed out.", ex);
            }

            response.EnsureSuccessStatusCode();

            var results = new List<LidarrTrack>();
            await using var stream = await response.Content.ReadAsStreamAsync(linkedCts.Token).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: linkedCts.Token).ConfigureAwait(false);

            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in doc.RootElement.EnumerateArray())
                {
                    var track = new LidarrTrack
                    {
                        Id = t.TryGetProperty("id", out var tid) && tid.ValueKind == JsonValueKind.Number ? tid.GetInt32() : 0,
                        TrackNumber = t.TryGetProperty("trackNumber", out var tn) ? tn.GetString() ?? string.Empty : string.Empty,
                        Title = t.TryGetProperty("title", out var tt) ? tt.GetString() ?? string.Empty : string.Empty,
                        Duration = t.TryGetProperty("duration", out var dur) && dur.ValueKind == JsonValueKind.Number ? dur.GetInt32() : 0,
                        HasFile = t.TryGetProperty("hasFile", out var hf) && hf.ValueKind == JsonValueKind.True
                    };

                    results.Add(track);
                }
            }

            return results;
        }

        // Lookup artists by term and return raw JSON objects as strings
        public async Task<IList<string>> LookupArtistsRawAsync(string term)
        {
            if (string.IsNullOrWhiteSpace(_baseUrl) || string.IsNullOrWhiteSpace(_apiKey))
                throw new InvalidOperationException("Lidarr URL or API Key is not set.");

            var uriBuilder = new UriBuilder(_baseUrl);
            var basePath = uriBuilder.Path?.TrimEnd('/') ?? string.Empty;
            uriBuilder.Path = $"{basePath}/api/v1/artist/lookup".TrimStart('/');
            uriBuilder.Query = $"term={Uri.EscapeDataString(term)}";

            using var request = new HttpRequestMessage(HttpMethod.Get, uriBuilder.Uri);
            request.Headers.Add("X-Api-Key", _apiKey);

            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var results = new List<string>();
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    results.Add(el.GetRawText());
                }
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                results.Add(doc.RootElement.GetRawText());
            }

            return results;
        }
    }
}