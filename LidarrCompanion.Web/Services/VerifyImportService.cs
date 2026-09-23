using LidarrCompanion.Helpers;
using LidarrCompanion.Models;
using LidarrCompanion.Services;
using System.Collections.Concurrent;

namespace LidarrCompanion.Web.Services
{
    // Owns post-import verification: once Lidarr accepts an import command, this polls (up to
    // ImportRunner.MaxTrackFetchAttempts times, ImportRunner.TrackFetchDelayMs apart) until Lidarr
    // actually confirms the track file landed - the command being accepted isn't the same as the
    // file being placed. This used to run as a synchronous loop inside ImportRunner.RunPipelineAsync,
    // which could hold Process Actions' busy lock (pointer-events:none on the whole page) for up to
    // 2.5 minutes per batch. It now runs as a genuine background service (same shape as
    // PrefetchService) so Process Actions returns as soon as import commands are sent.
    //
    // Registered as both a singleton (TriageService enqueues into it and subscribes to its events -
    // see Program.cs) and a hosted service, same reasoning as PrefetchService: the hosted-service
    // registration is what actually runs ExecuteAsync, the singleton registration is what lets
    // TriageService resolve the exact same instance rather than a second, empty one.
    //
    // Reprocessing a stuck/failed VerifyImport action (the scenario this whole redesign was built
    // for) is just calling Enqueue again with the same action, reset - see
    // TriageService.ProcessImportAsync, which intercepts VerifyImport-typed rows before they ever
    // reach ImportRunner.PrepareImport/the backup step, rather than filtering them out deep inside
    // shared pipeline code.
    public class VerifyImportService : BackgroundService
    {
        private const int MaxConcurrency = 2;
        private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(1);

        // Keyed by FileId - the same identity ProposedActions are already matched by everywhere
        // else in this app (e.g. TriageService's `ProposedActions.FirstOrDefault(p => p.FileId == ...)`
        // pattern).
        private readonly ConcurrentDictionary<int, ProposedAction> _tracked = new();
        private readonly SemaphoreSlim _concurrency = new(MaxConcurrency, MaxConcurrency);

        // For CopyFileToSecondary only - a cheap, stateless instance (TriageService does the same
        // with its own ImportRunner).
        private readonly ImportRunner _importRunner = new();

        // Raised (from this service's own background thread, NOT a page's sync context) on every
        // retry tick - the tracked action's own RetryCount/LastRetryAttempt/Status fields are
        // updated in place on the same object already sitting in TriageService.ProposedActions
        // before this fires (mutating simple fields on an already-tracked object is the same
        // pattern the non-blocking manual-artist-match job already uses; only actually
        // adding/removing collection members needs the more careful InvokeAsync handling below).
        // Subscribers just need to re-render - the values are already current.
        public event Action? Changed;

        // Raised (background thread) once a tracked action reaches a terminal state (Success or
        // retries exhausted). Unlike Changed, handling this involves removing the action from
        // ProposedActions and invalidating prefetch caches - real collection mutation, so
        // TriageService forwards this to whichever page is listening to run via InvokeAsync,
        // mirroring the ArtistReleasesReady/RefreshSelectedArtistReleasesAsync pattern used for
        // the background artist-match job.
        public event Action<ProposedAction>? ActionSettled;

        public void Enqueue(ProposedAction verifyAction)
        {
            verifyAction.Status = ImportActionStatus.Verifying;
            verifyAction.ErrorMessage = string.Empty;
            _tracked[verifyAction.FileId] = verifyAction;
        }

        public bool IsTracking(int fileId) => _tracked.ContainsKey(fileId);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(ScanInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                var due = _tracked.Values.Where(IsDue).ToList();
                if (due.Count == 0) continue;

                var tasks = due.Select(async action =>
                {
                    await _concurrency.WaitAsync(stoppingToken).ConfigureAwait(false);
                    try { await ProcessOneAsync(action).ConfigureAwait(false); }
                    finally { _concurrency.Release(); }
                });

                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
        }

        private static bool IsDue(ProposedAction action) =>
            !action.LastRetryAttempt.HasValue ||
            (DateTime.Now - action.LastRetryAttempt.Value).TotalMilliseconds >= ImportRunner.TrackFetchDelayMs;

        private async Task ProcessOneAsync(ProposedAction action)
        {
            action.LastRetryAttempt = DateTime.Now;
            action.RetryCount++;

            Logger.Log($"Processing VerifyImport for: {action.OriginalFileName}", LogSeverity.Low,
                new { FileName = action.OriginalFileName, Attempt = action.RetryCount, Max = action.MaxRetries });

            var lidarr = new LidarrHelper(); // fresh instance per call - see the shared HttpClient.Timeout gotcha in CLAUDE.md
            var trackFile = await TryRetrieveTrackFile(action, lidarr).ConfigureAwait(false);

            if (trackFile != null)
            {
                action.Status = ImportActionStatus.Success;
                Logger.Log($"VerifyImport succeeded for: {action.OriginalFileName}", LogSeverity.Low, new { FileName = action.OriginalFileName });
                await TrySecondaryCopyForImport(action, trackFile).ConfigureAwait(false);
                Settle(action);
            }
            else if (action.RetryCount >= action.MaxRetries)
            {
                Logger.Log($"VerifyImport max retries reached: {action.OriginalFileName}", LogSeverity.High, new { FileName = action.OriginalFileName, Attempts = action.RetryCount });
                action.Status = ImportActionStatus.Failed;
                action.ErrorMessage = $"Could not verify import after {action.MaxRetries} attempts. Process Actions will retry it.";
                Settle(action);
            }
            else
            {
                Changed?.Invoke();
            }
        }

        private void Settle(ProposedAction action)
        {
            _tracked.TryRemove(action.FileId, out _);
            ActionSettled?.Invoke(action);
        }

        private static async Task<LidarrTrackFile?> TryRetrieveTrackFile(ProposedAction action, LidarrHelper lidarr)
        {
            try
            {
                var tracks = await lidarr.GetTracksByReleaseAsync(action.AlbumReleaseId).ConfigureAwait(false);
                var matched = tracks?.FirstOrDefault(t => t.Id == action.TrackId);

                if (matched != null && matched.TrackFileId > 0)
                {
                    var tf = await lidarr.GetTrackFileAsync(matched.TrackFileId).ConfigureAwait(false);
                    if (tf != null && !string.IsNullOrWhiteSpace(tf.Path))
                    {
                        Logger.Log($"Track file verified successfully: {tf.Path}", LogSeverity.Low, new { TrackFileId = tf.Id, Path = tf.Path });
                        return tf;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to get track: {ex.Message}", LogSeverity.Low, new { Attempt = action.RetryCount, Error = ex.Message });
            }

            return null;
        }

        private async Task TrySecondaryCopyForImport(ProposedAction a, LidarrTrackFile tf)
        {
            var copyEnabled = AppSettings.Current.GetTyped<bool>(SettingKey.CopyImportedFiles);
            var copyDestRoot = AppSettings.GetValue(SettingKey.CopyImportedFilesPath);

            if (!copyEnabled || string.IsNullOrWhiteSpace(copyDestRoot) || string.IsNullOrWhiteSpace(tf.Path))
                return;

            try
            {
                var resolvedTfPath = FileOperationsHelper.ResolveMappedPath(tf.Path, SettingKey.LibraryPathLidarr, true);
                Logger.Log($"Copying imported file to secondary location", LogSeverity.Low, new { SourcePath = tf.Path }, filePath: resolvedTfPath);
                var outcome = _importRunner.CopyFileToSecondary(resolvedTfPath, a, SettingKey.CopyImportedFiles);

                if (outcome == ImportRunner.CopyOutcome.Failed)
                    Logger.Log($"Secondary copy failed for imported file", LogSeverity.Medium, new { ResolvedPath = resolvedTfPath, FileName = a.OriginalFileName }, filePath: resolvedTfPath);
            }
            catch (Exception ex)
            {
                Logger.Log($"Exception during secondary copy: {ex.Message}", LogSeverity.Medium, new { FileName = a.OriginalFileName, Error = ex.Message });
            }

            await Task.CompletedTask;
        }
    }
}
