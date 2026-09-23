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
        private readonly VerifyImportService _verifyImport;
        private readonly ImportRunner _importRunner = new();
        private readonly ProposalService _proposalService = new();

        public TriageService(StatusService status, CoverArtGateService coverArtGate, PrefetchService prefetch, VerifyImportService verifyImport)
        {
            _status = status;
            _coverArtGate = coverArtGate;
            _prefetch = prefetch;
            _verifyImport = verifyImport;

            // Forwarding only - VerifyImportService fires these from its own background thread, and
            // neither handler here touches an ObservableCollection or anything UI-bound (that would
            // be unsafe off the page's sync context). VerifyProgressChanged just tells a listening
            // page "something ticked, re-render"; VerifyActionSettled hands the settled action to
            // whichever page is listening, which must marshal via InvokeAsync before calling
            // ApplySettledVerifyActionAsync - same pattern as ArtistJobsChanged/ArtistReleasesReady
            // for the background artist-match job.
            _verifyImport.Changed += () => VerifyProgressChanged?.Invoke();
            _verifyImport.ActionSettled += settled => VerifyActionSettled?.Invoke(settled);
        }

        public event Action? VerifyProgressChanged;
        public event Action<ProposedAction>? VerifyActionSettled;

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

            // Without this, a caller whose work starts with synchronous, blocking I/O (backup file
            // copies chief among them) never gives the Blazor circuit's dispatcher a chance to
            // actually flush the pending "busy" render before that blocking work starts - the state
            // change happens, but it sits queued behind the very call that's about to block the
            // thread for however long the copy takes. Real symptom: hitting Process Actions with a
            // slow backup felt like nothing happened for several seconds, not "locked and busy".
            await Task.Yield();

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
        // under one busy message, auto-matches queue records to artists, then queues background
        // prefetch jobs (see PrefetchService) for every record's files and every matched artist's
        // releases so selecting a record a moment later is instant.
        //
        // Deliberately does NOT touch PrefetchService's caches - EnqueueFilePrefetches/
        // EnqueueMatchedArtistPrefetches below are no-ops for anything already cached, so a plain
        // Refresh re-polls Lidarr's queue/artist lists without throwing away every already-fetched
        // release's files or artist's tracks (a real complaint: Refresh used to wipe all of that
        // too, not just the top list). RefreshQueueAfterProcessingAsync is the targeted
        // counterpart that runs after Process Actions and invalidates only what was actually touched.
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
                    var queueOk = FetchQueueRecordsAsync();
                    var artistsOk = FetchArtistsAsync();
                    var folderCheck = CheckFolderAccessAsync();
                    var results = await Task.WhenAll(queueOk, artistsOk);
                    var folderIssues = await folderCheck;

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

                    // A folder problem is the more urgent thing to surface - it's why processing
                    // would fail later, and StatusService only ever shows one message at a time.
                    if (folderIssues.Count > 0)
                    {
                        var summary = string.Join("; ", folderIssues.Select(i => $"{i.Label} ({i.Error})"));
                        _status.ShowError($"Can't access: {summary}");
                    }
                    else if (results[0] && results[1])
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

        // Checks every configured Companion-side folder this app actually reads/writes - built
        // after a real NAS permission problem went unnoticed until Process Actions hit it mid-batch
        // (see CLAUDE.md's nounix/CIFS permission gotcha - a plain Directory.Exists/listing can
        // look fine on a mount like that right up until an actual write is attempted). Runs on
        // Refresh, i.e. on app open once the 6h/new-session staleness window has passed (or on a
        // manual Refresh click) - not on every page navigation. Each check is genuinely blocking
        // I/O (FolderAccessCheck.Check), so each runs via Task.Run and all run in parallel; the
        // caller awaits this alongside the queue/artist fetch rather than after it, so it doesn't
        // add to Refresh's own wall time beyond whichever finishes last.
        private async Task<List<FolderAccessCheck.Result>> CheckFolderAccessAsync()
        {
            var toCheck = new List<(string Label, string? Path, bool RequireWrite)>
            {
                ("Import folder", AppSettings.GetValue(SettingKey.ImportPathCompanion), true),
                ("Download folder", AppSettings.GetValue(SettingKey.DownloadPathCompanion), true),
                ("Library folder", AppSettings.GetValue(SettingKey.LibraryPathCompanion), false),
                ("Backup folder", AppSettings.GetValue(SettingKey.BackupRootFolder), true),
                ("Secondary copy folder", AppSettings.GetValue(SettingKey.CopyImportedFilesPath), true),
                ("Sift folder", AppSettings.GetValue(SettingKey.SiftFolder), true),
            };

            foreach (var dest in Destinations)
                toCheck.Add(($"Destination \"{dest.Name}\"", dest.DestinationPath, true));

            var results = await Task.WhenAll(toCheck.Select(t =>
                Task.Run(() => FolderAccessCheck.Check(t.Label, t.Path, t.RequireWrite))));

            return results.Where(r => !r.Ok).ToList();
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

        // Runs after a successful (possibly partial) import pipeline. Targeted, not a blanket
        // RefreshAsync: invalidates only the prefetch-cache entries for releases/artists that were
        // actually touched (their disk contents or Lidarr-side track metadata genuinely changed),
        // then does a full queue+artist re-fetch so the top table drops fully-imported/removed
        // records and reflects Lidarr's real state - but carries forward every existing match by
        // release identity first, and only runs AutoMatch over the leftover *unmatched* records, so
        // an unrelated release's manual match a moment earlier doesn't get silently discarded (unlike
        // a full Refresh, which intentionally re-auto-matches everything - see RefreshAsync).
        // Captures MatchedArtist/Match per release identity so they can be carried forward onto
        // freshly-fetched replacement LidarrQueueRecord objects - FetchQueueRecordsAsync always
        // builds brand-new objects with no match info of their own.
        private List<(ImplicitUnlink.ReleaseKey Key, ReleaseMatchType Match, string MatchedArtist)> CaptureCurrentMatches() =>
            QueueRecords
                .Where(r => !string.IsNullOrWhiteSpace(r.MatchedArtist))
                .Select(r => (Key: new ImplicitUnlink.ReleaseKey(r.DownloadId ?? string.Empty, r.Title ?? string.Empty), r.Match, r.MatchedArtist))
                .ToList();

        private static void CarryForwardMatches(IEnumerable<LidarrQueueRecord> freshRecords,
            List<(ImplicitUnlink.ReleaseKey Key, ReleaseMatchType Match, string MatchedArtist)> oldMatches)
        {
            foreach (var record in freshRecords)
            {
                var old = oldMatches.FirstOrDefault(m => ImplicitUnlink.IsSameRelease(record.DownloadId, record.Title, m.Key));
                if (old.MatchedArtist is not (null or ""))
                {
                    record.Match = old.Match;
                    record.MatchedArtist = old.MatchedArtist;
                }
            }
        }

        // Reselects whichever record `selectedKey` now maps to in the just-refreshed QueueRecords,
        // reloading its files/tracks live - or clears the selection if that release is no longer in
        // Lidarr's queue at all (fully imported/removed). Shared by both refresh paths below.
        private async Task ReselectAfterQueueRefreshAsync(ImplicitUnlink.ReleaseKey? selectedKey)
        {
            var reselected = selectedKey is { } key
                ? QueueRecords.FirstOrDefault(r => ImplicitUnlink.IsSameRelease(r.DownloadId, r.Title, key))
                : null;

            if (reselected is not null)
                await OnQueueRecordSelectedAsync(reselected);
            else if (selectedKey is not null)
            {
                SelectedQueueRecord = null;
                SelectedFile = null;
                SelectedTrack = null;
                CheckedFileIds.Clear();
                ManualImportFiles.Clear();
                ArtistReleaseTracks.Clear();
            }
        }

        // Runs after Process Actions sends its synchronous batch (Unlink/Delete/Move done,
        // Import commands sent). Deliberately does NOT re-fetch Artists or run AutoMatch any more -
        // real complaint: this used to unconditionally re-pull Lidarr's entire artist list and run
        // AutoMatch over every still-unmatched record on every single Process Actions call, even
        // though importing files never changes who the artists are, all while holding the page's
        // busy lock - directly part of "quite a few steps before it's usable again". Artists/
        // AutoMatch now only happen via the manual Refresh button, which is what it's for; if a
        // genuinely new queue record needs matching, Refresh is how you'd get it anyway. Doesn't
        // touch LastLoadedUtc either, for the same reason - this isn't a full "get everything
        // fresh" pass. ArtistsToInvalidate is deliberately not checked here either: it only ever
        // matches a settled VerifyImport (Action==VerifyImport && Status==Success), and nothing
        // reaches that status synchronously in this method - a plain Import action only gets as
        // far as Sent here (see ProcessImportGroup); the background settle path
        // (ApplySettledVerifyActionAsync) is what actually covers a confirmed import.
        private async Task RefreshQueueAfterProcessingAsync(List<ProposedAction> processedActions)
        {
            foreach (var key in PostProcessInvalidation.ReleasesToInvalidate(processedActions))
            {
                var record = QueueRecords.FirstOrDefault(r => ImplicitUnlink.IsSameRelease(r.DownloadId, r.Title, key));
                if (record is not null)
                    _prefetch.InvalidateReleaseFiles(record.OutputPath);
            }

            var oldMatches = CaptureCurrentMatches();
            var selectedKey = SelectedQueueRecord is { } sel
                ? new ImplicitUnlink.ReleaseKey(sel.DownloadId ?? string.Empty, sel.Title ?? string.Empty)
                : (ImplicitUnlink.ReleaseKey?)null;

            if (!await FetchQueueRecordsAsync()) return;

            CarryForwardMatches(QueueRecords, oldMatches);
            EnqueueFilePrefetches();
            EnqueueMatchedArtistPrefetches();

            await ReselectAfterQueueRefreshAsync(selectedKey);
        }

        // Lightweight counterpart to the above, used when a background VerifyImport settles
        // successfully (see ApplySettledVerifyActionAsync). The synchronous refresh right after
        // sending an import command necessarily runs before Lidarr has actually confirmed/cleaned
        // up its own queue - a just-imported release predictably still shows there at that point.
        // This re-checks the queue once Lidarr really has confirmed it, so the release actually
        // drops off the top table instead of lingering until the next manual Refresh. No Artists
        // re-fetch, no AutoMatch pass, no prefetch re-enqueue here - none of those meaningfully
        // change just because one file's import was confirmed, and a settle-time full refresh would
        // be needless extra load on Lidarr for something a plain queue re-check already covers.
        // Returns whether `key` is still present in Lidarr's queue after a fresh fetch - true on a
        // failed fetch too, since "don't know" should never be read as "gone" (that would wrongly
        // clear a still-live selection). Deliberately does NOT reselect/reload the release's files
        // even if it's the current selection - see ApplySettledVerifyActionAsync, the only caller,
        // for why a live re-scan of the release folder is specifically wrong to do here.
        private async Task<bool> RefreshQueueRecordsOnlyAsync(ImplicitUnlink.ReleaseKey key)
        {
            var oldMatches = CaptureCurrentMatches();
            if (!await FetchQueueRecordsAsync()) return true;

            CarryForwardMatches(QueueRecords, oldMatches);
            return QueueRecords.Any(r => ImplicitUnlink.IsSameRelease(r.DownloadId, r.Title, key));
        }

        public async Task<ImportSummary> ProcessImportAsync()
        {
            if (ProposedActions.Count == 0)
            {
                _status.ShowError("No proposed actions to import.");
                return new ImportSummary(0, 0, 0, 0);
            }

            // Stuck/failed VerifyImport rows (background verification exhausted its retries, or is
            // still in flight from a previous click) are handled entirely separately: re-enqueuing
            // them into VerifyImportService is all "process actions" needs to do for these - no
            // backup, no pipeline, since nothing about re-checking with Lidarr touches the source
            // file (ImportActionRules.RequiresSourceFile is false for VerifyImport). This is what
            // makes reprocessing a stuck row "just re-verify" by construction, not by filtering it
            // out of a shared code path.
            var retryVerifies = ProposedActions.Where(pa => pa.Action == ProposalActionType.VerifyImport).ToList();
            foreach (var retry in retryVerifies)
            {
                retry.RetryCount = 0;
                retry.LastRetryAttempt = null;
                retry.ErrorMessage = string.Empty;
                _verifyImport.Enqueue(retry);
            }

            var hasPipelineActions = ProposedActions.Any(pa => pa.Action != ProposalActionType.VerifyImport);

            var summary = new ImportSummary(0, 0, 0, 0);
            bool paused = false;
            string? backupFailure = null;
            string? unlinkPlanFailure = null;
            int sentCount = 0;

            if (hasPipelineActions)
            {
                await RunBusyAsync("Sending actions to Lidarr...", async () =>
                {
                    // Before anything is backed up or moved: every file in a release being imported
                    // that the user left without an action becomes an Unlink, because Lidarr deletes
                    // whatever is left behind in the folder after importing from it.
                    unlinkPlanFailure = await AddImplicitUnlinksAsync();
                    if (unlinkPlanFailure is not null) return;

                    // Re-snapshot rather than reusing the list above: AddImplicitUnlinksAsync may
                    // have just added new Unlink rows to ProposedActions. The already-re-enqueued
                    // VerifyImport rows are excluded - they never touch PrepareImport/backup.
                    var actionsSnapshot = ProposedActions.Where(pa => pa.Action != ProposalActionType.VerifyImport).ToList();
                    var prepare = _importRunner.PrepareImport(actionsSnapshot, new ImportResult());

                    if (prepare.BackupFailed)
                    {
                        // TryBackupFiles already wrote Status=Failed/ErrorMessage directly onto the
                        // ProposedAction instances (actionsSnapshot shares references with
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
                    // every action type sets Status=Failed on itself regardless of type, which this
                    // scan catches uniformly. Imported is always 0 here now - a real Lidarr import
                    // is only confirmed once its VerifyImport action settles, asynchronously (see
                    // ApplySettledVerifyActionAsync), not synchronously within this call.
                    var totalFailed = ApplyImportResultsToProposedActions();
                    summary = new ImportSummary(0, result.MoveSuccessCount, result.SecondaryCopyCount, totalFailed);

                    sentCount = result.NewVerifyActions.Count;
                    foreach (var newVerify in result.NewVerifyActions)
                        _verifyImport.Enqueue(newVerify);

                    await RefreshQueueAfterProcessingAsync(actionsSnapshot);
                });
            }

            // When paused, the caller navigates straight to /cover-art - the page itself is the
            // message, so nothing is posted to the status bar.
            if (unlinkPlanFailure is not null)
                _status.ShowError(unlinkPlanFailure);
            else if (backupFailure is not null)
                _status.ShowError(backupFailure);
            else if (!paused)
                PostSendSummary(summary, sentCount, retryVerifies.Count);

            return summary;
        }

        // Reports what happened synchronously (moves/unlinks/deletes/failures, via the existing
        // ImportSummary) plus how many import commands were sent for background verification and
        // how many stuck rows are being re-checked - there's no confirmed import count to report
        // yet at this point, that arrives later per-file via ApplySettledVerifyActionAsync.
        private void PostSendSummary(ImportSummary summary, int sentCount, int retryCount)
        {
            var parts = new List<string>();
            if (summary.HasAnyResult) parts.Add(summary.BuildMessage());
            if (sentCount > 0) parts.Add($"{sentCount} sent to Lidarr - verifying in the background.");
            if (retryCount > 0) parts.Add($"Re-checking {retryCount} pending import(s).");
            if (parts.Count == 0) return;

            var message = string.Join(" ", parts);
            if (summary.Failed > 0) _status.ShowError(message);
            else _status.SetInfo(message);
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
            int sentCount = 0;
            await RunBusyAsync("Importing files to Lidarr...", async () =>
            {
                var result = await RunImportPipelineAsync(actionsSnapshot);
                var totalFailed = ApplyImportResultsToProposedActions();
                summary = new ImportSummary(0, result.MoveSuccessCount, result.SecondaryCopyCount, totalFailed);

                sentCount = result.NewVerifyActions.Count;
                foreach (var newVerify in result.NewVerifyActions)
                    _verifyImport.Enqueue(newVerify);

                await RefreshQueueAfterProcessingAsync(actionsSnapshot);
            });

            PostSendSummary(summary, sentCount, retryCount: 0);

            return summary;
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

            // Sent counts as "done displaying" the same as Success: a Sent Import action has been
            // superseded by its own VerifyImport row, which is what continues to represent this
            // file going forward (and settles later, in the background - see
            // ApplySettledVerifyActionAsync). Verifying/Pending rows are left alone here; they're
            // still in progress.
            var allProcessedActions = ProposedActions.Where(pa =>
                pa.Status is ImportActionStatus.Success or ImportActionStatus.Sent or ImportActionStatus.Failed
            ).ToList();

            foreach (var pa in allProcessedActions)
            {
                if (ApplySingleActionOutcome(pa))
                    failedCount++;
            }

            AssignedFileIds.Clear();
            AssignedTrackIds.Clear();

            return failedCount;
        }

        // Removes a settled action from ProposedActions (Success/Sent) or leaves it visible with
        // its error surfaced via MatchedRelease (Failed). Returns true when it was a failure, so
        // callers can tally without duplicating the branch. Shared by the synchronous processing
        // pass above and the background-verify settlement path below.
        private bool ApplySingleActionOutcome(ProposedAction pa)
        {
            if (pa.Status is ImportActionStatus.Success or ImportActionStatus.Sent)
            {
                if (ProposedActions.Contains(pa)) ProposedActions.Remove(pa);
                return false;
            }

            if (pa.Status == ImportActionStatus.Failed)
            {
                pa.MatchedRelease = pa.ErrorMessage;
                return true;
            }

            return false;
        }

        // Called by the Import page (via InvokeAsync, from its VerifyActionSettled subscription -
        // see TriageService's constructor) once a background VerifyImport action reaches Success or
        // exhausts its retries. This is where a real Lidarr import is actually confirmed - the
        // pipeline that sent the command only got as far as Status=Sent (see ProcessImportGroup).
        // Must run on the page's sync context: it removes from ProposedActions (an
        // ObservableCollection) and may reload ManualImportFiles/ArtistReleaseTracks, neither of
        // which is safe to do from VerifyImportService's own background thread.
        public async Task ApplySettledVerifyActionAsync(ProposedAction settled)
        {
            var failed = ApplySingleActionOutcome(settled);

            if (failed)
                _status.ShowError($"{settled.OriginalFileName}: {settled.ErrorMessage}");
            else
                _status.SetInfo($"Verified import: {settled.OriginalFileName}");

            var releaseKey = new ImplicitUnlink.ReleaseKey(settled.DownloadId ?? string.Empty, settled.OriginalRelease ?? string.Empty);
            var record = QueueRecords.FirstOrDefault(r => ImplicitUnlink.IsSameRelease(r.DownloadId, r.Title, releaseKey));

            if (record is not null)
                _prefetch.InvalidateReleaseFiles(record.OutputPath);
            if (!failed && !string.IsNullOrWhiteSpace(settled.MatchedArtist))
                _prefetch.InvalidateArtistTracks(settled.MatchedArtist);

            if (!failed)
            {
                // A confirmed import is when Lidarr actually drops the record from its own queue -
                // re-check for that so a fully-imported release actually leaves the top table. This
                // deliberately never re-scans the release folder: by now the just-imported file is
                // gone from it (for a single-file release, the whole path is gone), so asking Lidarr
                // to re-scan it is not something to rely on - confirmed live that this path is
                // exactly the one that broke for a single-file release like a lone track import.
                var stillInQueue = await RefreshQueueRecordsOnlyAsync(releaseKey);

                if (SelectedQueueRecord is not null && ImplicitUnlink.IsSameRelease(SelectedQueueRecord.DownloadId, SelectedQueueRecord.Title, releaseKey))
                {
                    if (!stillInQueue)
                    {
                        // The whole release is done and gone from Lidarr's queue - nothing left to
                        // show for it.
                        SelectedQueueRecord = null;
                        SelectedFile = null;
                        SelectedTrack = null;
                        CheckedFileIds.Clear();
                        ManualImportFiles.Clear();
                        ArtistReleaseTracks.Clear();
                    }
                    else
                    {
                        // Still in the queue - other files in this release are still pending. Drop
                        // just the one file we already know is done from Unimported Release Files
                        // (no need to ask Lidarr to re-scan the folder to learn what we already know).
                        var fileRow = ManualImportFiles.FirstOrDefault(f => f.Id == settled.FileId);
                        if (fileRow is not null) ManualImportFiles.Remove(fileRow);

                        // The artist's track list only depends on Lidarr's album/track data, not on
                        // the release folder, so this reload is safe even though the file is gone.
                        if (!string.IsNullOrWhiteSpace(settled.MatchedArtist))
                            await LoadArtistReleasesAsync(settled.MatchedArtist);
                    }
                }
            }
            else if (record is not null && SelectedQueueRecord == record)
            {
                // A failure doesn't mean Lidarr's queue changed, and the import never actually went
                // through - the source file should still be there, so a live reload is fine here.
                await OnQueueRecordSelectedAsync(record);
            }
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
