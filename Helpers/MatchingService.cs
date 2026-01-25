using LidarrCompanion.Models;
using LidarrCompanion.Services;
using System.IO;
using System.Text.RegularExpressions;

namespace LidarrCompanion.Helpers
{
    public static class MatchingService
    {
        public static async Task<IList<LidarrAlbum>> GetAlbumsForArtistAsync(int artistId)
        {
            var lidarr = new LidarrHelper();
            return await lidarr.GetAlbumsByArtistAsync(artistId);
        }

        // Use FileAndAudioService.Normalize for string normalization
        public static string Normalize(string s)
        {
            return FileAndAudioService.Normalize(s);
        }

        // Auto match logic moved from MainWindow
        public static void AutoMatchReleasesToArtists(IList<LidarrQueueRecord> queueRecords, IList<LidarrArtist> artists, string importPath)
        {
            if (artists == null || artists.Count == 0)
                throw new ArgumentException("No artists provided", nameof(artists));

            foreach (var record in queueRecords)
            {
                record.Match = ReleaseMatchType.None;
                record.MatchedArtist = string.Empty;

                var lastFolder = FileAndAudioService.GetLowestFolderName(record.OutputPath);
                if (string.IsNullOrWhiteSpace(lastFolder))
                    continue;

                var normalizedFolder = Normalize(lastFolder);

                bool isSingleFile = FileAndAudioService.IsSingleFileRelease(record.OutputPath, importPath);

                if (isSingleFile)
                {
                    string fileName = Path.GetFileName(record.OutputPath) ?? lastFolder;
                    var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
                    if (string.IsNullOrWhiteSpace(nameWithoutExt))
                        continue;

                    var normalizedName = Normalize(nameWithoutExt);

                    var exact = artists.FirstOrDefault(a => Normalize(a.ArtistName) == normalizedName);
                    if (exact != null)
                    {
                        record.Match = ReleaseMatchType.Exact;
                        record.MatchedArtist = exact.ArtistName;
                        continue;
                    }

                    var dashIndex = nameWithoutExt.IndexOf('-');
                    if (dashIndex > 0)
                    {
                        var left = nameWithoutExt.Substring(0, dashIndex).Trim();
                        var normalizedLeft = Normalize(left);
                        var leftMatch = artists.FirstOrDefault(a => Normalize(a.ArtistName) == normalizedLeft);
                        if (leftMatch != null)
                        {
                            record.Match = ReleaseMatchType.ArtistFirstFile;
                            record.MatchedArtist = leftMatch.ArtistName;
                            continue;
                        }
                    }

                    var lastDash = nameWithoutExt.LastIndexOf('-');
                    if (lastDash > 0 && lastDash < nameWithoutExt.Length - 1)
                    {
                        var right = nameWithoutExt.Substring(lastDash + 1).Trim();
                        var normalizedRight = Normalize(right);
                        var rightMatch = artists.FirstOrDefault(a => Normalize(a.ArtistName) == normalizedRight);
                        if (rightMatch != null)
                        {
                            record.Match = ReleaseMatchType.AlbumFirstFile;
                            record.MatchedArtist = rightMatch.ArtistName;
                            continue;
                        }
                    }
                }
                else
                {
                    var exact = artists.FirstOrDefault(a => Normalize(a.ArtistName) == normalizedFolder);
                    if (exact != null)
                    {
                        record.Match = ReleaseMatchType.Exact;
                        record.MatchedArtist = exact.ArtistName;
                        continue;
                    }

                    var dashIndex = lastFolder.IndexOf('-');
                    if (dashIndex > 0)
                    {
                        var left = lastFolder.Substring(0, dashIndex).Trim();
                        var normalizedLeft = Normalize(left);
                        var leftMatch = artists.FirstOrDefault(a => Normalize(a.ArtistName) == normalizedLeft);
                        if (leftMatch != null)
                        {
                            record.Match = ReleaseMatchType.ArtistFirst;
                            record.MatchedArtist = leftMatch.ArtistName;
                            continue;
                        }
                    }

                    var lastDash = lastFolder.LastIndexOf('-');
                    if (lastDash > 0 && lastDash < lastFolder.Length - 1)
                    {
                        var right = lastFolder.Substring(lastDash + 1).Trim();
                        var normalizedRight = Normalize(right);
                        var rightMatch = artists.FirstOrDefault(a => Normalize(a.ArtistName) == normalizedRight);
                        if (rightMatch != null)
                        {
                            record.Match = ReleaseMatchType.AlbumFirst;
                            record.MatchedArtist = rightMatch.ArtistName;
                            continue;
                        }
                    }
                }
            }
        }

        // Build artist releases/tracks from UI collections (MainWindow passes them)
        public static (List<LidarrAlbum> Albums, Dictionary<int, IList<LidarrTrack>> TracksByRelease) BuildAlbumsFromUiCollections(
            IEnumerable<LidarrManualImportFile> files, IEnumerable<LidarrArtistReleaseTrack> artistReleaseTracks, string artistName)
        {
            var tracksByReleaseId = new Dictionary<int, IList<LidarrTrack>>();
            var releases = new List<LidarrAlbumRelease>();

            var grouped = artistReleaseTracks
                .GroupBy(x => new { x.ReleaseId, x.Release })
                .OrderBy(g => g.Key.Release);

            foreach (var g in grouped)
            {
                var releaseId = g.Key.ReleaseId;
                var releaseTitle = g.Key.Release ?? string.Empty;

                releases.Add(new LidarrAlbumRelease
                {
                    Id = releaseId,
                    Title = releaseTitle,
                    Format = string.Empty,
                    Country = string.Empty,
                    PublishDate = string.Empty
                });

                var trackList = g.Select(x => new LidarrTrack
                {
                    Id = x.TrackId,
                    TrackNumber = string.Empty,
                    Title = x.Track,
                    Duration = 0,
                    HasFile = x.HasFile
                }).ToList();

                tracksByReleaseId[releaseId] = trackList;
            }

            var albums = new List<LidarrAlbum>
            {
                new LidarrAlbum
                {
                    Id = 0,
                    Title = artistName,
                    Releases = releases
                }
            };

            return (albums, tracksByReleaseId);
        }

        public static async Task<IList<ProposedAction>> AiMatchAsync(IEnumerable<LidarrManualImportFile> candidateFiles,
            IEnumerable<LidarrArtistReleaseTrack> artistReleaseTracks,
            IList<LidarrAlbum> albums,
            IDictionary<int, IList<LidarrTrack>> tracksByReleaseId,
            string matchedArtist,
            LidarrQueueRecord selectedRecord,
            HashSet<int> assignedFileIds,
            HashSet<int> assignedTrackIds)
        {
            var results = new List<ProposedAction>();

            var ollama = new OllamaHelper();

            // call Ollama
            var matches = await ollama.MatchFilesToTracksAsync(candidateFiles.ToList(), albums, tracksByReleaseId);
            if (matches == null || !matches.Any())
                return results;

            // Build lookup from UI rows
            var trackLookup = artistReleaseTracks.ToDictionary(
                t => t.TrackId,
                t => (ReleaseDisplay: t.Release ?? string.Empty, TrackLabel: t.Track ?? string.Empty, HasFile: t.HasFile),
                EqualityComparer<int>.Default);

            foreach (var m in matches)
            {
                if (m == null) continue;
                if (m.FileId == 0 || m.TrackId == 0) continue;
                if (assignedFileIds.Contains(m.FileId) || assignedTrackIds.Contains(m.TrackId)) continue;

                var file = candidateFiles.FirstOrDefault(f => f.Id == m.FileId);
                if (file == null) continue;

                if (!trackLookup.TryGetValue(m.TrackId, out var info)) continue;

                // Mark file as assigned via proposed action type for UI highlighting
                file.ProposedActionType = ProposalActionType.Import;
                assignedFileIds.Add(file.Id);

                var artistTrackRow = artistReleaseTracks.FirstOrDefault(t => t.TrackId == m.TrackId);
                if (artistTrackRow != null)
                {
                    artistTrackRow.IsAssigned = true;
                    assignedTrackIds.Add(artistTrackRow.TrackId);
                }
                else
                {
                    assignedTrackIds.Add(m.TrackId);
                }

                var originalRelease = selectedRecord?.Title ?? Path.GetFileName(file.Path) ?? string.Empty;

                results.Add(new ProposedAction
                {
                    OriginalFileName = file.Name,
                    OriginalRelease = originalRelease,
                    MatchedArtist = matchedArtist,
                    MatchedTrack = info.TrackLabel,
                    MatchedRelease = info.ReleaseDisplay,
                    TrackId = m.TrackId,
                    FileId = file.Id,
                    Path = file.Path,
                    // carry through download id and quality info
                    DownloadId = selectedRecord?.DownloadId ?? string.Empty,
                    Quality = file.Quality
                });
            }

            return results;
        }

        // New: produce manual match candidates from a search source and artist list
        public static IList<LidarrArtist> GetManualMatchCandidates(string searchSource, IList<LidarrArtist> artists)
        {
            if (string.IsNullOrWhiteSpace(searchSource) || artists == null || artists.Count == 0)
                return new List<LidarrArtist>();

            var rawWords = Regex.Split(searchSource, @"\W+")
                .Where(w => w.Length >= 2)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var normalizedWords = rawWords.Select(w => Normalize(w)).Where(w => !string.IsNullOrWhiteSpace(w)).ToList();

            var candidates = artists
                .Where(a =>
                {
                    var n = Normalize(a.ArtistName);
                    return normalizedWords.Any(w => n.Contains(w, StringComparison.OrdinalIgnoreCase));
                })
                .OrderBy(a => a.ArtistName)
                .ToList();

            return candidates;
        }

        // New: load artist releases and tracks and return UI-ready rows
        public static async Task<IList<LidarrArtistReleaseTrack>> GetArtistReleaseTracksAsync(int artistId, HashSet<int> assignedTrackIds)
        {
            var results = new List<LidarrArtistReleaseTrack>();

            var lidarr = new LidarrHelper();
            var albums = await lidarr.GetAlbumsByArtistAsync(artistId);

            foreach (var album in albums)
            {
                foreach (var release in album.Releases)
                {
                    var tracks = await lidarr.GetTracksByReleaseAsync(release.Id);

                    var releaseDisplay = release.GetDisplayText();

                    foreach (var track in tracks)
                    {
                        var trackLabel = string.IsNullOrWhiteSpace(track.TrackNumber) ? track.Title : $"{track.TrackNumber}. {track.Title}";

                        var tr = new LidarrArtistReleaseTrack
                        {
                            Release = releaseDisplay,
                            Track = trackLabel,
                            HasFile = track.HasFile,
                            TrackId = track.Id,
                            ReleaseId = release.Id,
                            IsAssigned = assignedTrackIds.Contains(track.Id)
                        };

                        results.Add(tr);
                    }
                }
            }

            return results;
        }

        public static int ParseTrackNumber(string trackLabel)
        {
            if (string.IsNullOrWhiteSpace(trackLabel)) return int.MaxValue;
            var m = Regex.Match(trackLabel.Trim(), "^(\\d+)");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var n)) return n;
            return int.MaxValue;
        }

        public static string StripTrackNumberPrefix(string trackLabel)
        {
            if (string.IsNullOrWhiteSpace(trackLabel)) return string.Empty;
            return Regex.Replace(trackLabel, "^\\s*\\d+\\.?\\s*-?\\s*", string.Empty).Trim();
        }

        public static double WordMatchScore(string a, string b, double maxPoints)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return 0.0;
            var aw = a.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
            var bw = b.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
            if (aw.Length == 0 || bw.Length == 0) return 0.0;

            // Count how many unique words in A appear in B
            var matched = aw.Count(w => bw.Any(x => string.Equals(x, w, StringComparison.OrdinalIgnoreCase)));
            var percent = (double)matched / Math.Max(aw.Length, bw.Length);
            // scale to maxPoints
            return percent * maxPoints;
        }

        public static double ComputeMatchScore(string fileName, LidarrArtistReleaseTrack track, string artistName)
        {
            // Score components: Direct, Exact, Clean, Minimal, MinClean
            if (string.IsNullOrWhiteSpace(fileName) || track == null) return 0.0;

            var fileBase = fileName; //TODO - THIS CAN PROBABLY BE REMOVED. Files will already have the ext removed by the caller. Folders won't have them anwyay.  // Path.GetFileNameWithoutExtension(fileName) ?? fileName;
            var trackTitle = StripTrackNumberPrefix(track.Track);

            // Build reference variants to compare against. Include artist, release and combinations where available.
            var references = new List<string>();
            if (!string.IsNullOrWhiteSpace(artistName))
                references.Add($"{artistName} - {trackTitle}");

            if (!string.IsNullOrWhiteSpace(track.Release))
                references.Add($"{track.Release} - {trackTitle}");

            if (!string.IsNullOrWhiteSpace(artistName) && !string.IsNullOrWhiteSpace(track.Release))
                references.Add($"{artistName} - {track.Release} - {trackTitle}");

            // Always consider plain track title as fallback
            references.Add(trackTitle);

            double bestOverall = 0.0;

            // Load configured max scores from settings once
            var maxDirect = AppSettings.Current.GetTyped<int>(SettingKey.Direct);
            var maxExact = AppSettings.Current.GetTyped<int>(SettingKey.Exact);
            var maxClean = AppSettings.Current.GetTyped<int>(SettingKey.Clean);
            var maxMinimal = AppSettings.Current.GetTyped<int>(SettingKey.Minimal);
            var maxMinClean = AppSettings.Current.GetTyped<int>(SettingKey.MinClean);

            foreach (var trackReference in references.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct())
            {
                // Compute individual component scores and take the best among them for this reference
                double directScore = 0.0;
                double exactScore = 0.0;
                double cleanScore = 0.0;
                double minimalScore = 0.0;
                double minCleanScore = 0.0;

                var fileWords = Regex.Split(fileBase ?? string.Empty, "[^A-Za-z0-9]+")
                    .Select(w => w.Trim())
                    .Where(w => w.Length > 0)
                    .ToArray();
                var trackWords = Regex.Split(trackReference ?? string.Empty, "[^A-Za-z0-9]+")
                    .Select(w => w.Trim())
                    .Where(w => w.Length > 0)
                    .ToArray();

                // DIRECT: all words from fileBase appear in trackReference (order not required)
                if (fileWords.Length > 0 && fileWords.All(fw => trackWords.Any(tw => string.Equals(fw, tw, StringComparison.OrdinalIgnoreCase))))
                {
                    directScore = maxDirect; // direct match boost
                }

                // Exact full-string match
                if (string.Equals(fileBase, trackReference, StringComparison.OrdinalIgnoreCase))
                {
                    exactScore = maxExact;
                }

                // CLEAN paths
                var fileClean = CleanString(fileBase);
                var trackClean = CleanString(trackReference);
                cleanScore = WordMatchScore(fileClean, trackClean, maxClean);

                // MINIMAL paths
                var fileMin = MinimalString(fileClean);
                var trackMin = MinimalString(trackClean);
                minimalScore = WordMatchScore(fileMin, trackMin, maxMinimal);

                // MINCLEAN overlap
                var fileMinClean = MinimalString(fileBase);
                var trackMinClean = MinimalString(trackReference);
                minCleanScore = WordMatchScore(fileMinClean, trackMinClean, maxMinClean);

                double score = Math.Max(Math.Max(Math.Max(Math.Max(directScore, exactScore), cleanScore), minimalScore), minCleanScore);

                if (score > bestOverall) bestOverall = score;
            }

            // Do not apply release-level boost here; caller handles contextual boosts based on assigned files/tracks.
            return bestOverall;
        }

        public static string CleanString(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            var lowered = s.ToLowerInvariant();
            // remove common filler words
            var fillers = new[] { "feat", "featuring", "ft", "and", "x" };
            foreach (var f in fillers)
            {
                lowered = Regex.Replace(lowered, $"\\b{Regex.Escape(f)}\\b", string.Empty, RegexOptions.IgnoreCase);
            }
            // remove punctuation
            lowered = Regex.Replace(lowered, @"[.,;:!""'()\[\]/\\\-_]+", " ");
            lowered = Regex.Replace(lowered, @"\s+", " ").Trim();
            return lowered;
        }

        public static string MinimalString(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;

            // Work per-word so we can drop trailing 's' from each word
            var words = s.ToLowerInvariant()
                .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            var resultWords = new List<string>(words.Length);
            foreach (var word in words)
            {
                var sb = new System.Text.StringBuilder();
                char prev = '\0';
                foreach (var ch in word)
                {
                    // remove vowels
                    if ("aeiou".IndexOf(ch) >= 0)
                        continue;

                    // collapse double letters
                    if (ch == prev)
                        continue;

                    sb.Append(ch);
                    prev = ch;
                }

                // Drop trailing 's' if present
                var processed = sb.ToString();
                if (processed.EndsWith("s", StringComparison.Ordinal))
                {
                    processed = processed.Substring(0, processed.Length - 1);
                }

                if (!string.IsNullOrEmpty(processed))
                    resultWords.Add(processed);
            }

            return string.Join(' ', resultWords);
        }
    }
}
