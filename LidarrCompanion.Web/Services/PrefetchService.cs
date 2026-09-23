using LidarrCompanion.Helpers;
using LidarrCompanion.Models;
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace LidarrCompanion.Web.Services
{
    public enum PrefetchJobType { QueueRecordFiles, ArtistReleases }

    // Background worker that proactively warms the cache TriageService reads from when a queue
    // record is selected or an artist's releases are needed, so those feel instant instead of
    // waiting on a live Lidarr round-trip. Runs as a genuine background thread (a hosted
    // BackgroundService draining a Channel), never inline with a user request, and throttles
    // itself - GetFilesInReleaseAsync makes Lidarr do a live filesystem scan per call, so firing
    // a hundred of those at once (one per queue record) would hammer a self-hosted instance.
    //
    // Registered as both a singleton (so TriageService can inject it directly to enqueue work and
    // read the cache) and a hosted service (so its ExecuteAsync loop actually runs) - see
    // Program.cs.
    public class PrefetchService : BackgroundService
    {
        private const int MaxConcurrency = 2;

        private readonly Channel<PrefetchJob> _channel = Channel.CreateUnbounded<PrefetchJob>();
        private readonly ConcurrentDictionary<string, List<LidarrManualImportFile>> _filesByOutputPath = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, List<LidarrArtistReleaseTrack>> _tracksByArtistName = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte> _queued = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _concurrency = new(MaxConcurrency, MaxConcurrency);
        private int _generation;

        private record PrefetchJob(PrefetchJobType Type, LidarrQueueRecord? Record, LidarrArtist? Artist);

        // Drops everything cached so far. Bumping the generation means a fetch that was already in
        // flight when this ran can't write its (now stale) result back into the freshly-emptied
        // cache.
        public void Reset()
        {
            Interlocked.Increment(ref _generation);
            _filesByOutputPath.Clear();
            _tracksByArtistName.Clear();
            _queued.Clear();
        }

        // Targeted invalidation, used after Process Actions instead of a blanket Reset(): only the
        // release/artist that was actually just modified on disk needs a fresh fetch next time.
        public void InvalidateReleaseFiles(string outputPath)
        {
            if (string.IsNullOrWhiteSpace(outputPath)) return;
            _filesByOutputPath.TryRemove(outputPath, out _);
            _queued.TryRemove("files:" + outputPath, out _);
        }

        public void InvalidateArtistTracks(string artistName)
        {
            var key = MatchingService.Normalize(artistName);
            if (string.IsNullOrWhiteSpace(key)) return;
            _tracksByArtistName.TryRemove(key, out _);
            _queued.TryRemove("artist:" + key, out _);
        }

        public void EnqueueQueueRecordFiles(LidarrQueueRecord record)
        {
            if (string.IsNullOrWhiteSpace(record.OutputPath)) return;
            if (_filesByOutputPath.ContainsKey(record.OutputPath)) return;
            if (!_queued.TryAdd("files:" + record.OutputPath, 0)) return;

            _channel.Writer.TryWrite(new PrefetchJob(PrefetchJobType.QueueRecordFiles, record, null));
        }

        public void EnqueueArtistReleases(LidarrArtist artist)
        {
            var key = MatchingService.Normalize(artist.ArtistName);
            if (string.IsNullOrWhiteSpace(key)) return;
            if (_tracksByArtistName.ContainsKey(key)) return;
            if (!_queued.TryAdd("artist:" + key, 0)) return;

            _channel.Writer.TryWrite(new PrefetchJob(PrefetchJobType.ArtistReleases, null, artist));
        }

        // Returns the cached list if a background job (or an earlier call to this same method)
        // already fetched it; otherwise fetches live and populates the cache for next time. Used
        // by TriageService for both the background prefetch jobs and an on-demand cache miss (a
        // queue record the background worker hasn't gotten to yet), so there's exactly one code
        // path that actually talks to Lidarr for this data.
        public async Task<List<LidarrManualImportFile>> GetOrFetchQueueRecordFilesAsync(LidarrQueueRecord record)
        {
            if (_filesByOutputPath.TryGetValue(record.OutputPath, out var cached))
                return cached;

            var generation = Volatile.Read(ref _generation);
            var lidarr = new LidarrHelper();
            var files = (await lidarr.GetFilesInReleaseAsync(record.OutputPath)).ToList();
            if (generation == Volatile.Read(ref _generation))
                _filesByOutputPath[record.OutputPath] = files;
            return files;
        }

        public async Task<List<LidarrArtistReleaseTrack>> GetOrFetchArtistTracksAsync(LidarrArtist artist)
        {
            var key = MatchingService.Normalize(artist.ArtistName);
            if (_tracksByArtistName.TryGetValue(key, out var cached))
                return cached;

            var generation = Volatile.Read(ref _generation);
            var lidarr = new LidarrHelper();
            var tracks = await FetchArtistReleaseTracksAsync(artist, lidarr);

            // An empty list is not cached: an artist that was only just added to Lidarr has no
            // albums until Lidarr's own metadata refresh finishes, and caching that emptiness would
            // keep showing "no releases" for the artist until the next full refresh.
            if (tracks.Count > 0 && generation == Volatile.Read(ref _generation))
                _tracksByArtistName[key] = tracks;
            else if (tracks.Count == 0)
                _queued.TryRemove("artist:" + key, out _);

            return tracks;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await foreach (var job in _channel.Reader.ReadAllAsync(stoppingToken))
            {
                await _concurrency.WaitAsync(stoppingToken);
                _ = ProcessJobAsync(job).ContinueWith(_ => _concurrency.Release(), TaskScheduler.Default);
            }
        }

        private async Task ProcessJobAsync(PrefetchJob job)
        {
            try
            {
                if (job.Type == PrefetchJobType.QueueRecordFiles && job.Record is not null)
                {
                    await GetOrFetchQueueRecordFilesAsync(job.Record);
                }
                else if (job.Type == PrefetchJobType.ArtistReleases && job.Artist is not null)
                {
                    await GetOrFetchArtistTracksAsync(job.Artist);
                }
            }
            catch (Exception ex)
            {
                // Best-effort: the same fetch will just happen live (and get cached then) if the
                // user actually selects this record/artist before a retry would help.
                Logger.Log($"Background prefetch failed: {ex.Message}", LogSeverity.Low,
                    new { JobType = job.Type.ToString(), Error = ex.Message });
            }
        }

        private static async Task<List<LidarrArtistReleaseTrack>> FetchArtistReleaseTracksAsync(LidarrArtist artist, LidarrHelper lidarr)
        {
            var result = new List<LidarrArtistReleaseTrack>();
            var albums = await MatchingService.GetAlbumsForArtistAsync(artist.Id);

            foreach (var album in albums)
            {
                foreach (var release in album.Releases)
                {
                    var tracks = await lidarr.GetTracksByReleaseAsync(release.Id);
                    var releaseDisplay = release.GetDisplayText();

                    foreach (var track in tracks)
                    {
                        var trackLabel = string.IsNullOrWhiteSpace(track.TrackNumber) ? track.Title : $"{track.TrackNumber}. {track.Title}";

                        result.Add(new LidarrArtistReleaseTrack
                        {
                            Release = releaseDisplay,
                            Track = trackLabel,
                            HasFile = track.HasFile,
                            TrackId = track.Id,
                            ReleaseId = release.Id,
                            AlbumId = album.Id,
                            AlbumPath = album.Path ?? string.Empty,
                            ReleasePath = release.Path ?? string.Empty,
                            AlbumType = album.AlbumType ?? string.Empty
                        });
                    }
                }
            }

            return result;
        }
    }
}
