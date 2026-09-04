using LidarrCompanion.Models;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace LidarrCompanion.Helpers
{
    public class OllamaHelper
    {
        private readonly HttpClient _client;
        private readonly string _baseUrl;

        public OllamaHelper()
        {
            _baseUrl = AppSettings.GetValue(SettingKey.OllamaURL).TrimEnd('/');
            _client = new HttpClient();
            _client.Timeout = TimeSpan.FromMinutes(15);
        }

        // onChunk, when supplied, is invoked with each streamed response fragment as it arrives -
        // lets a host (e.g. a Blazor page) show live streaming output. Previously this wrote
        // straight to Console, which is invisible to anything but a container's stdout.
        public async Task<string> SendPromptAsync(string prompt, Action<string>? onChunk = null)
        {
            var requestBody = new
            {
                model = AppSettings.GetValue(SettingKey.OllamaModel),
                prompt = prompt, // $"Do not provide reasoning, just give the answer. {prompt}",
                stream = true // Enable streaming
            };
            var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
            using var response = await _client.PostAsync($"{_baseUrl}/api/generate", content);
            response.EnsureSuccessStatusCode();

            var resultBuilder = new StringBuilder();
            using var stream = await response.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream);

            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("response", out var responseElement))
                {
                    var chunk = responseElement.GetString();
                    resultBuilder.Append(chunk);
                    if (chunk != null) onChunk?.Invoke(chunk);
                }
            }

            return resultBuilder.ToString();
        }

        /// <summary>
        /// Ask the Ollama model to match downloaded files to release tracks.
        /// - `files` is the list of downloaded files (Id, Name).
        /// - `albums` is a list of albums containing releases.
        /// - `tracksByReleaseId` maps a release Id to the list of tracks for that release.
        ///
        /// The assistant is instructed to return only confident matches and to prefer tracks
        /// that come from the same release when possible. The method expects the AI to return
        /// a JSON array of objects with `fileId` and `trackId`.
        /// </summary>
        public async Task<IList<LidarrAiMatchResult>> MatchFilesToTracksAsync(
            IList<LidarrManualImportFile> files,
            IList<LidarrAlbum> albums,
            IDictionary<int, IList<LidarrTrack>> tracksByReleaseId,
            Action<string>? onChunk = null)
        {
            // Build a minimal, structured JSON payload the model can consume.
            var downloadedFilesObj = files.Select(f => new { id = f.Id, name = f.Name }).ToList();

            var releasesAndTracks = new List<object>();
            foreach (var album in albums)
            {
                foreach (var release in album.Releases)
                {
                    IList<object> tracks;
                    if (tracksByReleaseId != null && tracksByReleaseId.TryGetValue(release.Id, out var tlist))
                    {
                        tracks = tlist
                            .Select(t => (object)new { id = t.Id, trackNumber = t.TrackNumber, title = t.Title, duration = t.Duration })
                            .ToList();
                    }
                    else
                    {
                        tracks = new List<object>();
                    }

                    releasesAndTracks.Add(new
                    {
                        albumId = album.Id,
                        albumTitle = album.Title,
                        releaseId = release.Id,
                        releaseTitle = release.Title,
                        format = release.Format,
                        country = release.Country,
                        tracks
                    });
                }
            }

            var filesJson = JsonSerializer.Serialize(downloadedFilesObj);
            var releasesJson = JsonSerializer.Serialize(releasesAndTracks);

            // Fixed prompt with clear, strict instructions to the model.
            var promptBuilder = new StringBuilder();
            promptBuilder.AppendLine("Match downloaded files to release tracks.");
            promptBuilder.AppendLine("Only return confident matches. Prefer picking tracks from the same release/album where possible.");
            promptBuilder.AppendLine("Output exactly one JSON array and nothing else. Each array item must be an object with properties:");
            promptBuilder.AppendLine("{ \"fileId\": <downloaded file id>, \"trackId\": <matched track id> }");
            promptBuilder.AppendLine("Do not include duplicates for the same file. Do not include any explanation or extra fields.");
            promptBuilder.AppendLine();
            promptBuilder.AppendLine("DownloadedFiles:");
            promptBuilder.AppendLine(filesJson);
            promptBuilder.AppendLine();
            promptBuilder.AppendLine("ReleasesAndTracks:");
            promptBuilder.AppendLine(releasesJson);

            // The model usually receives an instruction prefix; include it explicitly so SendPromptAsync can stream accurately.
            var fullPrompt = $"Do not provide reasoning, just give the answer. {promptBuilder}";

            Logger.Log("Sending Ollama match prompt", LogSeverity.Verbose, new { PromptLength = fullPrompt.Length });

            // Send and stream response; the optional onChunk callback receives chunks as they arrive.
            var response = await SendPromptAsync(fullPrompt, onChunk);

            // Try to parse AI response to our expected structure.
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

            // First try to deserialize whole response directly
            try
            {
                var direct = JsonSerializer.Deserialize<IList<LidarrAiMatchResult>>(response, options);
                if (direct != null)
                    return direct;
            }
            catch
            {
                // fall through to extraction attempt
            }

            // If the model wrapped the JSON or added noise, try to extract the first JSON array substring.
            var firstBracket = response.IndexOf('[');
            var lastBracket = response.LastIndexOf(']');
            if (firstBracket >= 0 && lastBracket > firstBracket)
            {
                var jsonArray = response.Substring(firstBracket, lastBracket - firstBracket + 1);
                try
                {
                    var parsed = JsonSerializer.Deserialize<IList<LidarrAiMatchResult>>(jsonArray, options);
                    if (parsed != null)
                        return parsed;
                }
                catch
                {
                    // ignore parse errors and fall through
                }
            }

            // If nothing could be parsed, return an empty list.
            return new List<LidarrAiMatchResult>();
        }

        // Simple health check/warmup that sends a short prompt and returns true if any non-empty response was returned.
        public async Task<(bool Success, string Response)> CheckOllamaAsync(Action<string>? onChunk = null)
        {
            var prompt = "Health check: respond with the single word OK.";
            Logger.Log("Sending Ollama health check prompt", LogSeverity.Verbose);

            var response = await SendPromptAsync(prompt, onChunk);
            if (!string.IsNullOrWhiteSpace(response))
                return (true, response.Trim());
            return (false, response ?? string.Empty);
        }
    }

    public class LidarrAiMatchResult
    {
        public int FileId { get; set; }
        public int TrackId { get; set; }
    }
}
