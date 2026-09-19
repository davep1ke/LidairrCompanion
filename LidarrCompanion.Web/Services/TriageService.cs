using LidarrCompanion.Helpers;
using LidarrCompanion.Models;
using LidarrCompanion.Services;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;

namespace LidarrCompanion.Web.Services
{
    public enum MarkMatchResult
    {
        Success,
        NeedsFileSelection,
        NeedsTrackSelection,
        AlreadyAssigned,
        NeedsOverwriteConfirmation
    }

    // Registered as a Singleton (matching the app's single-active-session assumption), not
    // Scoped - a plain HTTP endpoint (e.g. the /audio/stream/{id} route Sift and track-preview
    // use) runs in its own per-request DI scope, separate from the Blazor circuit's scope, so a
    // Scoped registration here would hand that endpoint an empty, unrelated instance instead of
    // the one the triage page is actually using. Singleton means both sides see the same state.
    // No IJSRuntime here for the same reason - it's circuit-scoped, so it can't be constructor-
    // injected into a singleton; JS calls (e.g. the overwrite-confirm prompt) live in the
    // component instead, gated by MarkMatchResult.NeedsOverwriteConfirmation.
    //
    // In the WPF app this state all lived as MainWindow instance fields/partial-class methods,
    // safe because the window never got recreated; a Blazor page component does get recreated on
    // navigation, so this state needs to live somewhere that survives that.
    public class TriageService
    {
        private readonly StatusService _status;
        private readonly CoverArtGateService _coverArtGate;
        private readonly PrefetchService _prefetch;
        private readonly ImportRunner _importRunner = new();
        private readonly ProposalService _proposalService = new();

        public TriageService(StatusService status, CoverArtGateService coverArtGate, PrefetchService prefetch)
        {
            _status = status;
            _coverArtGate = coverArtGate;
            _prefetch = prefetch;
        }

        public ObservableCollection<LidarrQueueRecord> QueueRecords { get; } = new();
        public List<LidarrArtist> Artists { get; private set; } = new();
        public ObservableCollection<LidarrManualImportFile> ManualImportFiles { get; } = new();
        public ObservableCollection<LidarrArtistReleaseTrack> ArtistReleaseTracks { get; } = new();
        public ObservableCollection<ProposedAction> ProposedActions { get; } = new();
        public HashSet<int> AssignedFileIds { get; } = new();
        public HashSet<int> AssignedTrackIds { get; } = new();

        public LidarrQueueRecord? SelectedQueueRecord { get; set; }
        public LidarrManualImportFile? SelectedFile { get; set; }
        public HashSet<int> CheckedFileIds { get; } = new();
        public LidarrArtistReleaseTrack? SelectedTrack { get; set; }
        public ProposedAction? SelectedProposedAction { get; set; }

        public bool IsBusy => _status.IsBusy;
        public string SortMode { get; set; } = "by Best";

        public List<ImportDestination> Destinations => AppSettings.Current.ImportDestinations ?? new List<ImportDestination>();

        private async Task RunBusyAsync(string message, Func<Task> work)
        {
            _status.SetBusy(message);
            try
            {
                await work();
            }
            finally
            {
                _status.ClearBusy();
            }
        }

        #region Queue / Artists

        // When the queue/artists were last successfully loaded (null = never, or invalidated by a
        // new sign-in). Home and Import call NeedsRefresh on arrival: anything older than
        // RefreshPolicy.DefaultMaxAge, or loaded before the current sign-in, is re-fetched instead
        // of showing yesterday's queue.
        public DateTime? LastLoadedUtc { get; private set; }

        public bool NeedsRefresh => RefreshPolicy.IsStale(LastLoadedUtc, DateTime.UtcNow, RefreshPolicy.DefaultMaxAge);

        // Called on sign-in so a new session always starts from fresh Lidarr data.
        public void MarkSessionStale() => LastLoadedUtc = null;

        private int _refreshing;

        // Single entry point for "get current data": fetches the queue and artists concurrently
        // under one busy message, drops every prefetched cache (so nothing older than this refresh
        // survives), auto-matches queue records to artists, then queues background prefetch jobs
        // (see PrefetchService) for every record's files and every matched artist's releases so
        // selecting a record a moment later is instant.
        //
        // Auto-match used to be a separate button; it's always wanted straight after a refresh, and
        // a refresh replaces the queue record objects (losing any match), so it belongs here.
        public async Task RefreshAsync()
        {
            // Home and Import can both call this on arrival; one refresh at a time is enough.
            if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;

            try
            {
                await RunBusyAsync("Refreshing queue and artists from Lidarr...", async () =>
                {
                    _prefetch.Reset();

                    var queueOk = FetchQueueRecordsAsync();
                    var artistsOk = FetchArtistsAsync();
                    var results = await Task.WhenAll(queueOk, artistsOk);

                    SelectedQueueRecord = null;
                    SelectedFile = null;
                    SelectedTrack = null;
                    CheckedFileIds.Clear();
                    ManualImportFiles.Clear();
                    ArtistReleaseTracks.Clear();

                    if (results[0] && results[1])
                    {
                        LastLoadedUtc = DateTime.UtcNow;
                        await AutoMatchCoreAsync();
                    }

                    EnqueueFilePrefetches();
                    EnqueueMatchedArtistPrefetches();

                    if (results[0] && results[1])
                    {
                        var matched = QueueRecords.Count(r => !string.IsNullOrWhiteSpace(r.MatchedArtist));
                        _status.SetInfo($"Refreshed: {QueueRecords.Count} queue records ({matched} matched to an artist), {Artists.Count} artists.");
                    }
                });
            }
            finally
            {
                Interlocked.Exchange(ref _refreshing, 0);
            }
        }

        private async Task<bool> FetchQueueRecordsAsync()
        {
            try
            {
                Logger.Log("Fetching blocked/completed queue from Lidarr", LogSeverity.Medium);
                var lidarr = new LidarrHelper();
                var resultList = await lidarr.GetBlockedCompletedQueueAsync();

                QueueRecords.Clear();
                foreach (var record in resultList)
                    QueueRecords.Add(record);

                Logger.Log($"Retrieved {resultList.Count} queue records", LogSeverity.Low, new { Count = resultList.Count });
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to get files from Lidarr: {ex.Message}", LogSeverity.High, new { Error = ex.Message });
                _status.ShowError($"Get files failed: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> FetchArtistsAsync()
        {
            try
            {
                Logger.Log("Fetching all artists from Lidarr", LogSeverity.Medium);
                var lidarr = new LidarrHelper();
                var artists = await lidarr.GetAllArtistsAsync();
                Artists = artists.ToList();
                Logger.Log($"Successfully loaded {Artists.Count} artists", LogSeverity.Low, new { ArtistCount = Artists.Count });
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to get artists: {ex.Message}", LogSeverity.High, new { Error = ex.Message });
                _status.ShowError($"Get artists failed: {ex.Message}");
                return false;
            }
        }

        private void EnqueueFilePrefetches()
        {
            foreach (var record in QueueRecords)
                _prefetch.EnqueueQueueRecordFiles(record);
        }

        // Only records that already have a MatchedArtist (set by AutoMatch or a manual match, not
        // by Lidarr's own queue data - see ApplyManualMatchAsync/AutoMatchCoreAsync) have a known
        // artist worth prefetching releases for.
        private void EnqueueMatchedArtistPrefetches()
        {
            if (Artists.Count == 0) return;

            var matchedNames = QueueRecords
                .Select(r => r.MatchedArtist)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => MatchingService.Normalize(n))
                .Distinct()
                .ToList();

            foreach (var name in matchedNames)
            {
                var artist = Artists.FirstOrDefault(a => MatchingService.Normalize(a.ArtistName) == name);
                if (artist is not null)
                    _prefetch.EnqueueArtistReleases(artist);
            }
        }

        private async Task AutoMatchCoreAsync()
        {
            if (Artists.Count == 0) return;

            var importPath = AppSettings.GetValue(SettingKey.ImportPathLidarr);
            try
            {
                await Task.Run(() => MatchingService.AutoMatchReleasesToArtists(QueueRecords.ToList(), Artists, importPath));
            }
            catch (Exception ex)
            {
                _status.ShowError($"Auto match failed: {ex.Message}");
            }
        }

        // Bumped on every record selection. Loads capture it and drop their result if it has moved
        // on, so clicking through several releases quickly can never leave an earlier (slower)
        // release's files or tracks on screen - the last click always wins.
        private int _selectionVersion;

        // True from a record being selected until its files and artist tracks have arrived. Shown
        // as "Loading..." in the lists; deliberately NOT the global busy lock (see below).
        public bool IsLoadingSelection { get; private set; }

        // Deliberately does not take the global busy lock any more. The old version wrapped this in
        // RunBusyAsync, which greys out and disables the entire page - and a live Lidarr filesystem
        // scan for an uncached release (slow whenever Lidarr is busy, e.g. adding an artist) meant
        // the user couldn't click on to the next release until it finished. Now the record is
        // highlighted and the lists cleared immediately, loading continues in the background of the
        // page, and clicking another release simply supersedes it.
        public async Task OnQueueRecordSelectedAsync(LidarrQueueRecord? record)
        {
            var version = Interlocked.Increment(ref _selectionVersion);

            SelectedQueueRecord = record;
            SelectedFile = null;
            SelectedTrack = null;
            CheckedFileIds.Clear();
            ManualImportFiles.Clear();
            ArtistReleaseTracks.Clear();

            if (record == null)
            {
                IsLoadingSelection = false;
                return;
            }

            IsLoadingSelection = true;
            try
            {
                // Usually already warm - PrefetchService fetches every queue record's files in
                // the background right after Refresh. On a cache miss (record just appeared, or
                // the background worker hasn't reached it yet) this fetches live and caches the
                // result, same as before.
                var files = await _prefetch.GetOrFetchQueueRecordFilesAsync(record);
                if (version != _selectionVersion) return;

                foreach (var file in files)
                    ManualImportFiles.Add(file.Clone());

                foreach (var mf in ManualImportFiles)
                {
                    mf.ProposedActionType = null;
                    var pa = ProposedActions.FirstOrDefault(p => p.FileId == mf.Id);
                    if (pa != null) mf.ProposedActionType = pa.Action;
                }

                if (!string.IsNullOrWhiteSpace(record.MatchedArtist))
                    await LoadArtistReleasesAsync(record.MatchedArtist, version);
            }
            catch (Exception ex)
            {
                if (version == _selectionVersion)
                    _status.ShowError($"Failed to load files: {ex.Message}");
            }
            finally
            {
                if (version == _selectionVersion)
                    IsLoadingSelection = false;
            }
        }

        // selectionVersion: when given, the result is discarded if the selected record changed while
        // the tracks were being fetched. The collection is only touched after the await (cleared and
        // refilled in one synchronous step), so two overlapping loads can't interleave and leave
        // duplicated rows.
        public async Task LoadArtistReleasesAsync(string artistName, int? selectionVersion = null)
        {
            var artist = Artists.FirstOrDefault(a => MatchingService.Normalize(a.ArtistName) == MatchingService.Normalize(artistName));
            if (artist == null)
            {
                ArtistReleaseTracks.Clear();
                return;
            }

            try
            {
                // Same cache-or-fetch as above - warm whenever AutoMatch/manual match already
                // triggered a background prefetch for this artist.
                var tracks = await _prefetch.GetOrFetchArtistTracksAsync(artist);
                if (selectionVersion.HasValue && selectionVersion.Value != _selectionVersion) return;

                ArtistReleaseTracks.Clear();
                foreach (var track in tracks)
                {
                    var clone = track.Clone();
                    clone.IsAssigned = AssignedTrackIds.Contains(clone.TrackId);
                    ArtistReleaseTracks.Add(clone);
                }

                ApplySort(SortMode);
            }
            catch (Exception ex)
            {
                _status.ShowError($"Error loading artist releases: {ex.Message}");
            }
        }

        // Manual match: applied when the Match Artist dialog closes with a selection.
        //
        // Deliberately does NOT hold the busy lock. The selected record is marked matched
        // immediately, then everything slow - creating the artist in Lidarr if it isn't there yet,
        // waiting for Lidarr's own metadata refresh to produce its albums, loading its release
        // tracks - runs as a background job, so the user can move straight on to the next release
        // and match that too. Progress is visible via IsArtistPending (queue table / tracks table
        // show a waiting indicator) and ArtistReleasesReady tells the page when to refresh.
        public void ApplyManualMatch(LidarrQueueRecord record, LidarrArtist selected)
        {
            var previousMatch = record.Match;
            var previousArtist = record.MatchedArtist;

            record.Match = ReleaseMatchType.Exact;
            record.MatchedArtist = selected.ArtistName;

            var key = MatchingService.Normalize(selected.ArtistName);
            _pendingArtists[key] = selected.Id == 0
                ? $"Adding {selected.ArtistName} to Lidarr..."
                : $"Loading releases for {selected.ArtistName}...";
            ArtistJobsChanged?.Invoke();

            _ = Task.Run(() => RunManualMatchJobAsync(record, selected, previousMatch, previousArtist, key));
        }

        private const int NewArtistPollAttempts = 24;
        private static readonly TimeSpan NewArtistPollDelay = TimeSpan.FromSeconds(5);

        private async Task RunManualMatchJobAsync(LidarrQueueRecord record, LidarrArtist selected,
            ReleaseMatchType previousMatch, string? previousArtist, string key)
        {
            try
            {
                var artist = selected;
                var createdNew = selected.Id == 0;

                if (createdNew)
                {
                    var existing = Artists.FirstOrDefault(a => string.Equals(a.ForeignArtistId, selected.ForeignArtistId, StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(selected.ForeignArtistId));
                    if (existing is not null)
                    {
                        artist = existing;
                        createdNew = false;
                    }
                    else
                    {
                        artist = await CreateArtistInLidarrAsync(selected);
                    }
                }

                // Copy-on-write: the page and the manual match dialog read Artists while this runs
                // on another thread, so swap in a new list rather than mutating the shared one.
                var alreadyKnown = Artists.Any(a => (a.Id != 0 && artist.Id != 0 && a.Id == artist.Id) ||
                    string.Equals(MatchingService.Normalize(a.ArtistName), MatchingService.Normalize(artist.ArtistName), StringComparison.OrdinalIgnoreCase));
                if (!alreadyKnown)
                    Artists = new List<LidarrArtist>(Artists) { artist };

                // A freshly created artist has no albums until Lidarr finishes its own metadata
                // refresh, so wait (and re-check) for that; an artist Lidarr already had is one fetch.
                _pendingArtists[key] = $"Waiting for {artist.ArtistName}'s releases from Lidarr...";
                ArtistJobsChanged?.Invoke();

                var tracks = await Polling.UntilAsync(
                    () => _prefetch.GetOrFetchArtistTracksAsync(artist),
                    t => t.Count > 0,
                    createdNew ? NewArtistPollAttempts : 1,
                    NewArtistPollDelay);

                if (tracks.Count == 0)
                    _status.SetInfo($"{artist.ArtistName} was added, but Lidarr hasn't listed any releases for it yet. Select the release again in a minute.");
                else
                    _status.SetInfo($"{artist.ArtistName}: {tracks.Count} tracks ready.");
            }
            catch (Exception ex)
            {
                Logger.Log($"Manual artist match failed: {ex.Message}", LogSeverity.High, new { Artist = selected.ArtistName, Error = ex.Message });
                _status.ShowError($"Couldn't add {selected.ArtistName} to Lidarr: {ex.Message}");

                // Put the record back how it was - leaving it "matched" to an artist that doesn't
                // exist would mislead the next pass through the queue.
                record.Match = previousMatch;
                record.MatchedArtist = previousArtist ?? string.Empty;
            }
            finally
            {
                _pendingArtists.TryRemove(key, out _);
                ArtistJobsChanged?.Invoke();
            }

            ArtistReleasesReady?.Invoke(selected.ArtistName);
        }

        private static async Task<LidarrArtist> CreateArtistInLidarrAsync(LidarrArtist selected)
        {
            var artistName = selected.ArtistName;
            var foreignId = !string.IsNullOrWhiteSpace(selected.ForeignArtistId) ? selected.ForeignArtistId : Guid.NewGuid().ToString();
            var folder = string.IsNullOrWhiteSpace(artistName) ? "Unknown" : artistName;

            var rootFolder = AppSettings.GetValue(SettingKey.DefaultArtistRootFolder);
            var qualityProfileId = AppSettings.Current.GetTyped<int>(SettingKey.DefaultArtistQualityProfileId);
            var metadataProfileId = AppSettings.Current.GetTyped<int>(SettingKey.DefaultArtistMetadataProfileId);

            var lidarr = new LidarrHelper();
            var created = await lidarr.CreateArtistAsync(artistName, foreignId, folder, rootFolder, qualityProfileId, metadataProfileId, monitored: true, searchForMissingAlbums: true);
            return created ?? throw new InvalidOperationException("Lidarr did not return the created artist.");
        }

        private readonly ConcurrentDictionary<string, string> _pendingArtists = new();

        // Raised (from a background thread) when a manual-match job starts, changes stage or ends,
        // so the page can re-render its waiting indicators.
        public event Action? ArtistJobsChanged;

        // Raised (from a background thread) when a manual-match job has finished, whether or not it
        // found releases. The page calls RefreshSelectedArtistReleasesAsync on its own sync context.
        public event Action<string>? ArtistReleasesReady;

        public bool IsArtistPending(string? artistName) =>
            !string.IsNullOrWhiteSpace(artistName) && _pendingArtists.ContainsKey(MatchingService.Normalize(artistName));

        public string? PendingArtistMessage(string? artistName) =>
            !string.IsNullOrWhiteSpace(artistName) && _pendingArtists.TryGetValue(MatchingService.Normalize(artistName), out var msg) ? msg : null;

        // Reloads the tracks list, but only if the record on screen is the one that was matched
        // to this artist - the user may well have moved on to another release by now.
        public async Task RefreshSelectedArtistReleasesAsync(string artistName)
        {
            if (SelectedQueueRecord is null) return;
            if (!string.Equals(MatchingService.Normalize(SelectedQueueRecord.MatchedArtist), MatchingService.Normalize(artistName), StringComparison.OrdinalIgnoreCase))
                return;

            await LoadArtistReleasesAsync(artistName, _selectionVersion);
        }

        #endregion

        #region Sorting

        public void ApplySort(string mode)
        {
            SortMode = mode;
            IEnumerable<LidarrArtistReleaseTrack> items = ArtistReleaseTracks.ToList();

            switch (mode)
            {
                case "by Release":
                    foreach (var it in ArtistReleaseTracks) it.Score = 0.0;
                    items = items.OrderBy(i => i.Release).ThenBy(i => i.Track);
                    break;
                case "by Track Num":
                    foreach (var it in ArtistReleaseTracks) it.Score = 0.0;
                    items = items.OrderBy(i => MatchingService.ParseTrackNumber(i.Track)).ThenBy(i => i.Track);
                    break;
                case "by Track Name":
                    foreach (var it in ArtistReleaseTracks) it.Score = 0.0;
                    items = items.OrderBy(i => MatchingService.StripTrackNumberPrefix(i.Track)).ThenBy(i => i.Track);
                    break;
                case "by Best":
                    if (SelectedFile == null)
                    {
                        foreach (var it in ArtistReleaseTracks) it.Score = 0.0;
                        items = items.OrderBy(i => i.Release).ThenBy(i => i.Track);
                        break;
                    }

                    var selFileName = SelectedFile.Name;
                    var selFileId = SelectedFile.Id;
                    var selectedArtistName = SelectedQueueRecord?.MatchedArtist ?? string.Empty;

                    int releaseBoost = AppSettings.Current.GetTyped<int>(SettingKey.ReleaseBoost);
                    if (releaseBoost <= 0) releaseBoost = 10;

                    foreach (var i in ArtistReleaseTracks)
                    {
                        double baseScore = MatchingService.ComputeMatchScore(selFileName, i, selectedArtistName);

                        if (i.HasFile || SelectedFile.ProposedActionType == ProposalActionType.Import)
                        {
                            i.Score = baseScore;
                            continue;
                        }

                        var selectedFileAlreadyMappedToThisRelease = ProposedActions.Any(a => a.FileId == selFileId && a.AlbumReleaseId == i.ReleaseId);
                        if (selectedFileAlreadyMappedToThisRelease)
                        {
                            i.Score = baseScore;
                            continue;
                        }

                        bool otherAssignedInRelease = ArtistReleaseTracks.Any(t => t.ReleaseId == i.ReleaseId && t.IsAssigned && t.TrackId != i.TrackId)
                            || AssignedTrackIds.Any(id => ArtistReleaseTracks.Any(t => t.ReleaseId == i.ReleaseId && t.TrackId == id && t.TrackId != i.TrackId))
                            || ProposedActions.Any(a => a.AlbumReleaseId == i.ReleaseId && a.FileId != selFileId);

                        i.Score = otherAssignedInRelease ? baseScore + releaseBoost : baseScore;
                    }

                    items = items.OrderByDescending(i => i.Score).ThenBy(i => i.Release).ThenBy(i => i.Track);
                    break;
                default:
                    foreach (var it in ArtistReleaseTracks) it.Score = 0.0;
                    items = items.OrderBy(i => i.Release).ThenBy(i => i.Track);
                    break;
            }

            var selectedTrack = SelectedTrack;
            var ordered = items.ToList();
            ArtistReleaseTracks.Clear();
            foreach (var it in ordered)
                ArtistReleaseTracks.Add(it);

            if (selectedTrack != null)
                SelectedTrack = ArtistReleaseTracks.FirstOrDefault(t => t.TrackId == selectedTrack.TrackId && t.ReleaseId == selectedTrack.ReleaseId);

            UpdateReleaseAssignmentHighlights();
        }

        private void UpdateReleaseAssignmentHighlights()
        {
            var releasesWithAssigned = new HashSet<int>(ArtistReleaseTracks.Where(t => t.IsAssigned).Select(t => t.ReleaseId));

            foreach (var pa in ProposedActions)
            {
                if (pa.Action == ProposalActionType.Import && pa.AlbumReleaseId != 0)
                    releasesWithAssigned.Add(pa.AlbumReleaseId);
            }

            foreach (var tr in ArtistReleaseTracks)
                tr.ReleaseHasOtherAssigned = releasesWithAssigned.Contains(tr.ReleaseId) && !tr.IsAssigned;
        }

        #endregion

        #region AI Match

        public Task AiMatchAsync() => RunBusyAsync("Running AI matching...", async () =>
        {
            if (SelectedQueueRecord == null)
            {
                _status.ShowError("Select a release from the list first.");
                return;
            }
            if (string.IsNullOrWhiteSpace(SelectedQueueRecord.MatchedArtist))
            {
                _status.ShowError("Selected release has no matched artist. Use Auto or Manual match first.");
                return;
            }

            var candidateFiles = ManualImportFiles.Where(f => f.ProposedActionType != ProposalActionType.Import && !AssignedFileIds.Contains(f.Id)).ToList();
            if (candidateFiles.Count == 0)
            {
                _status.ShowError("No unassigned files available to match.");
                return;
            }

            var (albums, tracksByReleaseId) = MatchingService.BuildAlbumsFromUiCollections(ManualImportFiles, ArtistReleaseTracks, SelectedQueueRecord.MatchedArtist);

            try
            {
                var proposed = await MatchingService.AiMatchAsync(candidateFiles, ArtistReleaseTracks, albums, tracksByReleaseId, SelectedQueueRecord.MatchedArtist, SelectedQueueRecord, AssignedFileIds, AssignedTrackIds);

                if (proposed == null || proposed.Count == 0)
                {
                    _status.ShowError("No confident matches returned by the AI.");
                    return;
                }

                _proposalService.ApplyAiProposals(proposed, ArtistReleaseTracks, ManualImportFiles, Artists, ProposedActions, SelectedQueueRecord, AssignedFileIds, AssignedTrackIds);

                if (ProposedActions.Count == 0)
                    _status.ShowError("No confident matches could be applied (all matches were skipped or duplicates).");
            }
            catch (Exception ex)
            {
                _status.ShowError($"AI match failed: {ex.Message}");
            }
        });

        #endregion

        #region Mark Match

        // overwriteConfirmed: pass true only after the caller has already shown a JS confirm()
        // prompt in response to a prior NeedsOverwriteConfirmation result.
        public MarkMatchResult MarkMatch(bool overwriteConfirmed = false)
        {
            if (SelectedFile == null)
            {
                _status.ShowError("Select a file from 'Unimported Release Files' first.");
                return MarkMatchResult.NeedsFileSelection;
            }
            if (SelectedTrack == null)
            {
                _status.ShowError("Select a track from 'Artist Release Files' first.");
                return MarkMatchResult.NeedsTrackSelection;
            }
            if (SelectedFile.ProposedActionType == ProposalActionType.Import || AssignedFileIds.Contains(SelectedFile.Id))
            {
                _status.ShowError("This file has already been assigned.");
                return MarkMatchResult.AlreadyAssigned;
            }
            if (SelectedTrack.IsAssigned || AssignedTrackIds.Contains(SelectedTrack.TrackId))
            {
                _status.ShowError("This track has already been assigned.");
                return MarkMatchResult.AlreadyAssigned;
            }

            if (SelectedTrack.HasFile && !overwriteConfirmed)
            {
                return MarkMatchResult.NeedsOverwriteConfirmation;
            }

            _proposalService.CreateManualAssignment(SelectedFile, SelectedTrack, SelectedQueueRecord, Artists, ProposedActions, ManualImportFiles, AssignedFileIds, AssignedTrackIds);

            // Moving on to the next file (and re-sorting the tracks for it) is AdvanceAfterActionAsync's
            // job, shared with Unlink/Delete/Move - the caller invokes it after a Success.
            return MarkMatchResult.Success;
        }

        private bool IsFileHandled(LidarrManualImportFile f) =>
            f.ProposedActionType.HasValue || AssignedFileIds.Contains(f.Id);

        // After Mark Match / Unlink / Delete / Move: jump to the next file that still needs a
        // decision, so working through a release is a run of key presses with no clicking between.
        // Order of preference:
        //   1. the next unhandled file after the one just acted on (or the first unhandled one
        //      anywhere, if only earlier files were skipped);
        //   2. if the whole release is now handled, the next queue record, with its first file
        //      selected - so the user keeps flowing rather than stopping at every release boundary.
        //   3. otherwise nothing is selected.
        // The tracks list is re-sorted for whichever file ends up selected. Returns true when the
        // selection moved to a different file or record (the page scrolls the tracks list to top).
        public async Task<bool> AdvanceAfterActionAsync(LidarrManualImportFile? actedOn)
        {
            var files = ManualImportFiles.ToList();
            var currentIndex = actedOn is null ? -1 : files.IndexOf(actedOn);

            var next = FileSelection.NextUnhandledIndex(files.Count, currentIndex, i => IsFileHandled(files[i]));
            if (next >= 0)
            {
                SelectedFile = files[next];
                ApplySort(SortMode);
                return true;
            }

            var recordIndex = SelectedQueueRecord is null ? -1 : QueueRecords.IndexOf(SelectedQueueRecord);
            if (recordIndex >= 0 && recordIndex < QueueRecords.Count - 1)
            {
                var nextRecord = QueueRecords[recordIndex + 1];
                await OnQueueRecordSelectedAsync(nextRecord);

                // The user may have clicked a different release while that loaded; leave theirs alone.
                if (SelectedQueueRecord == nextRecord)
                {
                    SelectedFile = ManualImportFiles.FirstOrDefault(f => !IsFileHandled(f));
                    ApplySort(SortMode);
                }
                return true;
            }

            SelectedFile = null;
            ApplySort(SortMode);
            return false;
        }

        #endregion

        #region Proposals

        // Returns the files acted on (empty if nothing was selected), in list order.
        public List<LidarrManualImportFile> CreateProposal(ProposalActionType kind, string? destinationName = null)
        {
            var selectedFiles = ManualImportFiles.Where(f => CheckedFileIds.Contains(f.Id)).ToList();
            if (selectedFiles.Count == 0 && SelectedFile != null)
                selectedFiles = new List<LidarrManualImportFile> { SelectedFile };

            if (selectedFiles.Count == 0)
            {
                _status.ShowError("Select one or more files from 'Unimported Release Files' first.");
                return selectedFiles;
            }

            foreach (var selFile in selectedFiles)
            {
                var existingForFile = ProposedActions.Where(p => p.FileId == selFile.Id).ToList();
                foreach (var ex in existingForFile)
                {
                    var fileForEx = ManualImportFiles.FirstOrDefault(f => f.Id == ex.FileId);
                    if (fileForEx != null) fileForEx.ProposedActionType = null;
                    ProposedActions.Remove(ex);
                }

                var originalRelease = SelectedQueueRecord?.Title ?? Path.GetFileName(selFile.Path) ?? string.Empty;

                var pa = new ProposedAction
                {
                    OriginalFileName = selFile.Name,
                    OriginalRelease = originalRelease,
                    FileId = selFile.Id,
                    Path = selFile.Path,
                    DownloadId = SelectedQueueRecord?.DownloadId ?? string.Empty,
                    Action = kind,
                    DestinationName = destinationName ?? string.Empty
                };

                ProposedActions.Add(pa);
                selFile.ProposedActionType = kind;
            }

            CheckedFileIds.Clear();
            return selectedFiles;
        }

        public List<LidarrManualImportFile> MoveToDestination(ImportDestination dest) =>
            CreateProposal(ProposalActionType.MoveToDestination, dest.Name);

        public void Unselect(ProposedAction toRemove)
        {
            var file = ManualImportFiles.FirstOrDefault(f => f.Id == toRemove.FileId);
            var track = ArtistReleaseTracks.FirstOrDefault(t => t.TrackId == toRemove.TrackId && t.Release == toRemove.MatchedRelease);

            if (file != null)
            {
                file.ProposedActionType = null;
                AssignedFileIds.Remove(file.Id);
            }
            if (track != null)
            {
                track.IsAssigned = false;
                AssignedTrackIds.Remove(track.TrackId);
            }

            ProposedActions.Remove(toRemove);

            var releaseKey = toRemove.OriginalRelease ?? string.Empty;
            var hasAssignmentsForRelease = ProposedActions.Any(p => p.Action == ProposalActionType.Import && (p.OriginalRelease ?? string.Empty) == releaseKey);
            if (!hasAssignmentsForRelease)
            {
                var movesToRemove = ProposedActions.Where(p => p.Action == ProposalActionType.NotForImport && (p.OriginalRelease ?? string.Empty) == releaseKey).ToList();
                foreach (var m in movesToRemove)
                {
                    var f = ManualImportFiles.FirstOrDefault(x => x.Id == m.FileId);
                    if (f != null) f.ProposedActionType = null;
                    ProposedActions.Remove(m);
                }
            }
        }

        public void ClearProposed()
        {
            foreach (var f in ManualImportFiles)
                f.ProposedActionType = null;
            ProposedActions.Clear();
        }

        #endregion

        #region Import

        public async Task<ImportSummary> ProcessImportAsync()
        {
            if (ProposedActions.Count == 0)
            {
                _status.ShowError("No proposed actions to import.");
                return new ImportSummary(0, 0, 0, 0);
            }

            var summary = new ImportSummary(0, 0, 0, 0);
            bool paused = false;
            string? backupFailure = null;
            string? unlinkPlanFailure = null;

            await RunBusyAsync("Importing files to Lidarr...", async () =>
            {
                // Before anything is backed up or moved: every file in a release being imported that
                // the user left without an action becomes an Unlink, because Lidarr deletes whatever
                // is left behind in the folder after importing from it.
                unlinkPlanFailure = await AddImplicitUnlinksAsync();
                if (unlinkPlanFailure is not null) return;

                var actionsSnapshot = ProposedActions.ToList();
                var prepare = _importRunner.PrepareImport(actionsSnapshot, new ImportResult());

                if (prepare.BackupFailed)
                {
                    // TryBackupFiles already wrote ImportStatus="Failed"/ErrorMessage directly
                    // onto the ProposedAction instances (actionsSnapshot shares references with
                    // ProposedActions) - just apply the usual cleanup pass and report everything
                    // as failed (nothing got far enough to import/move/copy).
                    backupFailure = actionsSnapshot.Select(a => a.ErrorMessage).FirstOrDefault(m => !string.IsNullOrWhiteSpace(m));
                    var failedCount = ApplyImportResultsToProposedActions();
                    summary = new ImportSummary(0, 0, 0, failedCount);
                    return;
                }

                if (prepare.ArtItemsToVerify.Any(i => !i.HasCoverArt))
                {
                    // Pause here rather than awaiting the gate inline: this callback is running
                    // inside RunBusyAsync, and a human-timescale wait would leave the triage page
                    // behind pointer-events:none for as long as the user takes on /cover-art.
                    // Parking the state and returning lets the page stay usable; resolving the
                    // gate calls back into ResumeImportAfterCoverArtAsync/AbortImportAfterCoverArt.
                    _coverArtGate.BeginGate(prepare.ArtItemsToVerify, actionsSnapshot);
                    paused = true;
                    return;
                }

                var result = await RunImportPipelineAsync(actionsSnapshot);
                // The scan-based fail count (not result.FailCount) is authoritative for failures:
                // ImportRunner only increments FailCount for Import/VerifyImport failures, not
                // Move/Unlink/Delete ones, but every action type sets ImportStatus="Failed" on
                // itself regardless of type, which this scan catches uniformly.
                var totalFailed = ApplyImportResultsToProposedActions();
                summary = new ImportSummary(result.ImportSuccessCount, result.MoveSuccessCount, result.SecondaryCopyCount, totalFailed);
            });

            // When paused, the caller navigates straight to /cover-art - the page itself is the
            // message, so nothing is posted to the status bar.
            if (unlinkPlanFailure is not null)
                _status.ShowError(unlinkPlanFailure);
            else if (backupFailure is not null)
                _status.ShowError(BackupFailureHelp.Describe(backupFailure));
            else if (!paused && summary.HasAnyResult)
                PostImportSummary(summary);

            return summary;
        }

        // Applies the "no action = Unlink" rule (see ImplicitUnlink for why and for its scope).
        // Rebuilt from scratch on every run, so a release that has since lost its Import action (the
        // user hit Unselect after a failed run) doesn't keep unlinks it no longer needs.
        //
        // Returns an error message when the rule couldn't be applied safely. In that case nothing is
        // processed: importing anyway would let Lidarr delete the files that couldn't be accounted for.
        private async Task<string?> AddImplicitUnlinksAsync()
        {
            foreach (var old in ProposedActions.Where(a => a.IsImplicitUnlink).ToList())
            {
                var row = ManualImportFiles.FirstOrDefault(f => f.Id == old.FileId);
                if (row is not null) row.ProposedActionType = null;
                ProposedActions.Remove(old);
            }

            foreach (var key in ImplicitUnlink.ReleasesBeingImported(ProposedActions))
            {
                var record = QueueRecords.FirstOrDefault(r => ImplicitUnlink.IsSameRelease(r.DownloadId, r.Title, key));
                if (record is null)
                    return $"Couldn't find the queue record for '{key.OriginalRelease}' to check for unmatched files, so nothing was processed. Click Refresh and redo that release.";

                List<LidarrManualImportFile> files;
                try
                {
                    files = await _prefetch.GetOrFetchQueueRecordFilesAsync(record);
                }
                catch (Exception ex)
                {
                    Logger.Log($"Couldn't list files to unlink for '{record.Title}': {ex.Message}", LogSeverity.High, new { Release = record.Title, Error = ex.Message });
                    return $"Couldn't list the files in '{record.Title}' to unlink the unmatched ones ({ex.Message}), so nothing was processed - Lidarr would delete them.";
                }

                var unlinks = ImplicitUnlink.Build(files, ProposedActions, key.OriginalRelease, key.DownloadId,
                    f => FileOperationsHelper.ValidateFileExists(FileOperationsHelper.ResolveMappedPathAnyKnown(f.Path, true)));

                foreach (var unlink in unlinks)
                {
                    ProposedActions.Add(unlink);
                    var row = ManualImportFiles.FirstOrDefault(f => f.Id == unlink.FileId);
                    if (row is not null) row.ProposedActionType = ProposalActionType.Unlink;
                }

                if (unlinks.Count > 0)
                    Logger.Log($"Added {unlinks.Count} implicit Unlink action(s) for unmatched files in '{record.Title}'", LogSeverity.Medium, new { Release = record.Title, Count = unlinks.Count });
            }

            return null;
        }

        // Called by the Cover Art page's Finish Import / Save & Finish buttons once every item has
        // been addressed (not every file needs to end up with art - matching the WPF app, it is
        // available even if some are skipped).
        public async Task<ImportSummary> ResumeImportAfterCoverArtAsync()
        {
            var actionsSnapshot = _coverArtGate.CompleteAndTakeActions();
            if (actionsSnapshot is null)
            {
                _status.ShowError("No import was pending.");
                return new ImportSummary(0, 0, 0, 0);
            }

            var summary = new ImportSummary(0, 0, 0, 0);
            await RunBusyAsync("Importing files to Lidarr...", async () =>
            {
                var result = await RunImportPipelineAsync(actionsSnapshot);
                var totalFailed = ApplyImportResultsToProposedActions();
                summary = new ImportSummary(result.ImportSuccessCount, result.MoveSuccessCount, result.SecondaryCopyCount, totalFailed);
            });

            if (summary.HasAnyResult)
                PostImportSummary(summary);

            return summary;
        }

        // Reported via the status bar rather than a blocking alert() - same notification style as
        // everything else (e.g. "Downloaded N artists from Lidarr."), so a finished import doesn't
        // need its own dismissal click to get out of the way.
        private void PostImportSummary(ImportSummary summary)
        {
            if (summary.Failed > 0) _status.ShowError(summary.BuildMessage());
            else _status.SetInfo(summary.BuildMessage());
        }

        // Called by the Cover Art page's Abort button. Proposed actions remain unchanged - the
        // import simply never continued past the gate, matching the WPF app's abort behavior.
        public void AbortImportAfterCoverArt()
        {
            _coverArtGate.Abort();
            _status.ShowError("Import aborted - cover art was not resolved. Proposed actions were not changed.");
        }

        private async Task<ImportResult> RunImportPipelineAsync(List<ProposedAction> actionsSnapshot)
        {
            try
            {
                return await _importRunner.RunPipelineAsync(actionsSnapshot, ManualImportFiles, ProposedActions, ArtistReleaseTracks, AssignedFileIds, AssignedTrackIds);
            }
            catch (Exception ex)
            {
                Logger.Log($"Import exception: {ex.Message}", LogSeverity.Critical, new { Error = ex.Message });
                _status.ShowError($"Import failed: {ex.Message}");
                return new ImportResult();
            }
        }

        // Removes every successfully-processed action from the list and surfaces the error on
        // failed ones (via MatchedRelease, reusing that column for display) - returns the total
        // failed count. Success counts come from the real ImportResult instead (see callers).
        private int ApplyImportResultsToProposedActions()
        {
            int failedCount = 0;

            var allProcessedActions = ProposedActions.Where(pa =>
                !string.IsNullOrWhiteSpace(pa.ImportStatus) &&
                (pa.ImportStatus.Equals("Success", StringComparison.OrdinalIgnoreCase) || pa.ImportStatus.Equals("Failed", StringComparison.OrdinalIgnoreCase))
            ).ToList();

            foreach (var pa in allProcessedActions)
            {
                if (string.Equals(pa.ImportStatus, "Success", StringComparison.OrdinalIgnoreCase))
                {
                    if (ProposedActions.Contains(pa)) ProposedActions.Remove(pa);
                }
                else if (string.Equals(pa.ImportStatus, "Failed", StringComparison.OrdinalIgnoreCase))
                {
                    pa.MatchedRelease = pa.ErrorMessage;
                    failedCount++;
                }
            }

            AssignedFileIds.Clear();
            AssignedTrackIds.Clear();

            return failedCount;
        }

        #endregion

        #region Ollama

        public async Task<(bool success, string response)> CheckOllamaAsync()
        {
            (bool success, string response) result = (false, string.Empty);
            await RunBusyAsync("Checking Ollama...", async () =>
            {
                try
                {
                    var ollama = new OllamaHelper();
                    result = await ollama.CheckOllamaAsync();
                }
                catch (Exception ex)
                {
                    result = (false, ex.Message);
                }
            });
            return result;
        }

        #endregion
    }
}
