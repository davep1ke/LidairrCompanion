using LidarrCompanion.Models;
using System.Net.Http;
using System.Text.Json;

namespace LidarrCompanion.Helpers
{
    public class LidarrHelper
    {
        private readonly string _baseUrl;

        private readonly HttpClient _client;

        public LidarrHelper()
        {

            _client = new HttpClient();
            // Read timeout from settings (default30s)
            int timeoutSeconds = AppSettings.Current.GetTyped<int>(SettingKey.LidarrHttpTimeout);
            if (timeoutSeconds <= 0) timeoutSeconds = 30;
            _client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
        }

        // Centralised request builder + sender. Returns the response JSON as string.
        private async Task<string> CallLidarrAsync(HttpMethod method, string relativePath, object? payload = null)
        {
            var baseUrl = AppSettings.GetValue(SettingKey.LidarrURL)?.TrimEnd('/');
            var apiKey = AppSettings.GetValue(SettingKey.LidarrAPIKey);
            if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("Lidarr URL or API Key is not set.");

            var url = baseUrl + "/" + relativePath.TrimStart('/');

            // Ensure API key present as query parameter (caller-side removed apikey usage)
            var sep = url.Contains('?') ? '&' : '?';
            url = url + sep + "apikey=" + Uri.EscapeDataString(apiKey);

            using var request = new HttpRequestMessage(method, url);
            // keep header as well for compatibility
            request.Headers.Add("X-Api-Key", apiKey);

            if (payload != null)
            {
                var jsonPayload = JsonSerializer.Serialize(payload);
                request.Content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "application/json");
            }

            // Standardise to read full content before returning
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseContentRead).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var respJson = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return respJson;
        }

        public async Task<IList<LidarrQueueRecord>> GetBlockedCompletedQueueAsync(int page = 1, int pageSize = 50)
        {
            var relative = $"api/v1/queue?page={page}&pageSize={pageSize}&includeUnknownArtistItems=true&includeArtist=true&includeAlbum=true";
            var json = await CallLidarrAsync(HttpMethod.Get, relative).ConfigureAwait(false);

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
            var relative = $"api/v1/manualimport?folder={Uri.EscapeDataString(folderPath)}";
            var json = await CallLidarrAsync(HttpMethod.Get, relative).ConfigureAwait(false);

            var results = new List<LidarrManualImportFile>();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    var file = new LidarrManualImportFile
                    {
                        Path = item.GetProperty("path").GetString(),
                        Name = item.GetProperty("name").GetString(),
                        Id = item.GetProperty("id").GetInt32()
                    };

                    // parse quality/revision if present
                    if (item.TryGetProperty("quality", out var q) && q.ValueKind == JsonValueKind.Object)
                    {
                        var mq = new LidarrManualFileQuality();
                        if (q.TryGetProperty("quality", out var qinfo) && qinfo.ValueKind == JsonValueKind.Object)
                        {
                            var qi = new LidarrQualityInfo();
                            if (qinfo.TryGetProperty("id", out var qid) && qid.ValueKind == JsonValueKind.Number) qi.id = qid.GetInt32();
                            if (qinfo.TryGetProperty("name", out var qname) && qname.ValueKind == JsonValueKind.String) qi.name = qname.GetString();
                            mq.quality = qi;
                        }
                        if (q.TryGetProperty("revision", out var rev) && rev.ValueKind == JsonValueKind.Object)
                        {
                            var r = new LidarrRevision();
                            if (rev.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number) r.version = v.GetInt32();
                            if (rev.TryGetProperty("real", out var real) && real.ValueKind == JsonValueKind.Number) r.real = real.GetInt32();
                            if (rev.TryGetProperty("isRepack", out var ir) && ir.ValueKind == JsonValueKind.True) r.isRepack = true;
                            mq.revision = r;
                        }
                        file.Quality = mq;
                    }

                    results.Add(file);
                }
            }
            return results;
        }

        public async Task<IList<LidarrArtist>> GetAllArtistsAsync()
        {
            var json = await CallLidarrAsync(HttpMethod.Get, "api/v1/artist").ConfigureAwait(false);

            var results = new List<LidarrArtist>();

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    results.Add(new LidarrArtist
                    {
                        ArtistName = item.GetProperty("artistName").GetString(),
                        Id = item.GetProperty("id").GetInt32(),
                        Path = item.TryGetProperty("path", out var ap) && ap.ValueKind == JsonValueKind.String ? ap.GetString() ?? string.Empty : string.Empty
                    });
                }
            }

            return results;
        }

        // Get albums for an artist (includeAllArtistAlbums=true)
        public async Task<IList<LidarrAlbum>> GetAlbumsByArtistAsync(int artistId)
        {
            var relative = $"api/v1/album?artistId={artistId}&includeAllArtistAlbums=true";
            var json = await CallLidarrAsync(HttpMethod.Get, relative).ConfigureAwait(false);

            var results = new List<LidarrAlbum>();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    var album = new LidarrAlbum
                    {
                        Title = item.TryGetProperty("title", out var t) ? t.GetString() ?? string.Empty : string.Empty,
                        Id = item.TryGetProperty("id", out var id) ? id.GetInt32() : 0,
                        Path = item.TryGetProperty("path", out var ap) && ap.ValueKind == JsonValueKind.String ? ap.GetString() ?? string.Empty : string.Empty,
                        AlbumType = item.TryGetProperty("albumType", out var at) && at.ValueKind == JsonValueKind.String ? at.GetString() ?? string.Empty : string.Empty
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
                                PublishDate = r.TryGetProperty("publishDate", out var pd) ? pd.GetString() ?? string.Empty : string.Empty,
                                Path = r.TryGetProperty("path", out var rpath) && rpath.ValueKind == JsonValueKind.String ? rpath.GetString() ?? string.Empty : string.Empty
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
                results.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase));
            }

            return results;
        }

        // Get tracks for a given albumReleaseId
        public async Task<IList<LidarrTrack>> GetTracksByReleaseAsync(int albumReleaseId)
        {
            var relative = $"api/v1/track?albumReleaseId={albumReleaseId}";
            var json = await CallLidarrAsync(HttpMethod.Get, relative).ConfigureAwait(false);

            var results = new List<LidarrTrack>();
            using var doc = JsonDocument.Parse(json);
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
            var relative = $"api/v1/artist/lookup?term={Uri.EscapeDataString(term)}";
            var json = await CallLidarrAsync(HttpMethod.Get, relative).ConfigureAwait(false);

            var results = new List<string>();
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

        // Create a new artist in Lidarr by POSTing the provided raw JSON body (from lookup)
        public async Task<LidarrArtist?> CreateArtistAsync(string artistJson)
        {
            // Keep compatibility: forward to new overload by parsing minimal fields from provided JSON
            try
            {
                using var doc = JsonDocument.Parse(artistJson);
                var artistName = doc.RootElement.TryGetProperty("artistName", out var an) && an.ValueKind == JsonValueKind.String ? an.GetString() ?? string.Empty : string.Empty;
                string foreignId;
                if (doc.RootElement.TryGetProperty("foreignArtistId", out var fid) && fid.ValueKind == JsonValueKind.String)
                    foreignId = fid.GetString()!;
                else if (doc.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                    foreignId = id.GetString()!;
                else if (doc.RootElement.TryGetProperty("id", out var idn) && idn.ValueKind == JsonValueKind.Number)
                    foreignId = idn.GetInt32().ToString();
                else
                    foreignId = System.Guid.NewGuid().ToString();

                var folder = string.IsNullOrWhiteSpace(artistName) ? "Unknown" : artistName;

                // rootFolderPath, qualityProfileId, metadataProfileId and monitored are not available in the raw lookup JSON so use defaults
                return await CreateArtistAsync(artistName, foreignId, folder, "/mnt/Music/Albums", qualityProfileId: 1, metadataProfileId: 5, monitored: true, searchForMissingAlbums: true).ConfigureAwait(false);
            }
            catch
            {
                // If parsing fails, attempt to post the raw string directly as before
                var json = await CallLidarrAsync(HttpMethod.Post, "api/v1/artist", artistJson).ConfigureAwait(false);
                try
                {
                    using var doc2 = JsonDocument.Parse(json);
                    var artist = new LidarrArtist();
                    if (doc2.RootElement.TryGetProperty("artistName", out var an2) && an2.ValueKind == JsonValueKind.String)
                        artist.ArtistName = an2.GetString() ?? string.Empty;
                    else if (doc2.RootElement.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String)
                        artist.ArtistName = t.GetString() ?? string.Empty;

                    if (doc2.RootElement.TryGetProperty("id", out var id2) && id2.ValueKind == JsonValueKind.Number)
                        artist.Id = id2.GetInt32();

                    return artist;
                }
                catch
                {
                    return null;
                }
            }
        }

        // New overload: construct the JSON payload from parameters and POST to create artist
        public async Task<LidarrArtist?> CreateArtistAsync(string artistName, string foreignArtistId, string folder, string rootFolderPath, int qualityProfileId = 1, int metadataProfileId = 5, bool monitored = true, bool searchForMissingAlbums = true)
        {
            var payload = new
            {
                artistName = artistName,
                foreignArtistId = foreignArtistId,
                monitored = monitored,
                addOptions = new { searchForMissingAlbums = searchForMissingAlbums },
                qualityProfileId = qualityProfileId,
                metadataProfileId = metadataProfileId,
                rootFolderPath = rootFolderPath,
                folder = folder
            };

            var json = await CallLidarrAsync(HttpMethod.Post, "api/v1/artist", payload).ConfigureAwait(false);

            try
            {
                using var doc = JsonDocument.Parse(json);
                var artist = new LidarrArtist();
                if (doc.RootElement.TryGetProperty("artistName", out var an) && an.ValueKind == JsonValueKind.String)
                    artist.ArtistName = an.GetString() ?? string.Empty;
                else if (doc.RootElement.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String)
                    artist.ArtistName = t.GetString() ?? string.Empty;

                if (doc.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number)
                    artist.Id = id.GetInt32();

                return artist;
            }
            catch
            {
                // If parsing fails, return null but do not throw further
                return null;
            }
        }

        internal async Task<bool> ImportFilesAsync(List<ProposedAction> proposedActions)
        {
            if (proposedActions == null || proposedActions.Count == 0)
                return false;

            // Build files array - one entry per proposed action
            var files = new List<object>();
            foreach (var pa in proposedActions)
            {
                var fileEntry = new
                {
                    albumId = pa.AlbumId,
                    albumReleaseId = pa.AlbumReleaseId,
                    artistId = pa.ArtistId,
                    disableReleaseSwitching = false,
                    downloadId = pa.DownloadId ?? string.Empty,
                    indexerFlags = 0,
                    path = pa.Path ?? string.Empty,
                    trackIds = new[] { pa.TrackId },
                    quality = pa.Quality
                };

                files.Add(fileEntry);
            }

            var body = new
            {
                files = files,
                importMode = "auto",
                name = "ManualImport",
                replaceExistingFiles = true
            };

            try
            {
                var json = await CallLidarrAsync(HttpMethod.Post, "api/v1/command", body).ConfigureAwait(false);
                // if we got here the call succeeded
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}