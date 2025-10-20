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

        public async Task<IList<LidarrQueueRecord>> GetBlockedCompletedQueueAsync(int page = 1, int pageSize = 10)
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

        // Changed: use X-Api-Key header, use SendAsync with cancellation and ResponseHeadersRead,
        // and parse the response stream to avoid building a large string. Added optional CancellationToken.
        public async Task<IList<LidarrArtist>> GetAllArtistsAsync(CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(_baseUrl) || string.IsNullOrWhiteSpace(_apiKey))
                throw new InvalidOperationException("Lidarr URL or API Key is not set.");

            // Build the request URI safely
            var uriBuilder = new UriBuilder(_baseUrl);
            // Ensure there's exactly one slash between base and path
            var basePath = uriBuilder.Path?.TrimEnd('/') ?? string.Empty;
            uriBuilder.Path = $"{basePath}/api/v1/artist".TrimStart('/');

            using var request = new HttpRequestMessage(HttpMethod.Get, uriBuilder.Uri);
            // Many Lidarr/Sonarr/Radarr instances accept API keys in the X-Api-Key header.
            request.Headers.Add("X-Api-Key", _apiKey);

            // Link the caller's cancellation with an internal timeout based on HttpClient.Timeout
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linkedCts.CancelAfter(_client.Timeout);

            HttpResponseMessage response;
            try
            {
                response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Distinguish timeout from external cancellation
                throw new TimeoutException("Request to Lidarr timed out.", ex);
            }

            response.EnsureSuccessStatusCode();

            var results = new List<LidarrArtist>();

            // Parse directly from the response stream to avoid allocating a large string for big results
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
    }
}