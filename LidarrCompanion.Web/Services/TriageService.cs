using LidarrCompanion.Helpers;
using LidarrCompanion.Models;
using LidarrCompanion.Services;
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

        // Only auto-triggered once per app lifetime (see Home.razor) - subsequent visits to Home
        // shouldn't silently re-hit Lidarr every time. The Refresh button is the explicit re-run.
        public bool HasLoadedOnce { get; private set; }

        // Replaces the old separate "Get Next Files"/"Get Artists" buttons: both matter equally
        // for keeping the triage screen current, and running them concurrently under one busy
        // message is both faster and simpler than two independent buttons users had to remember
        // to press together. Once both land, queues up background prefetch jobs (see
        // PrefetchService) for every queue record's files and every already-matched artist's
        // releases, so selecting a record a moment later is instant instead of a fresh Lidarr
        // round-trip.
        public Task RefreshAsync() => RunBusyAsync("Refreshing queue and artists from Lidarr...", async () =>
        {
            HasLoadedOnce = true;

            var queueOk = FetchQueueRecordsAsync();
            var artistsOk = FetchArtistsAsync();
            var results = await Task.WhenAll(queueOk, artistsOk);

            EnqueueFilePrefetches();
            EnqueueMatchedArtistPrefetches();

            if (results[0] && results[1])
                _status.SetInfo($"Refreshed: {QueueRecords.Count} queue records, {Artists.Count} artists.");
        });

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
        // by Lidarr's own queue data - see ApplyManualMatchAsync/AutoMatchAsync) have a known
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

        public Task AutoMatchAsync() => RunBusyAsync("Auto-matching releases to artists...", async () =>
        {
            if (Artists.Count == 0)
            {
                _status.ShowError("No artists loaded. Click 'Refresh' first.");
                return;
            }

            var importPath = AppSettings.GetValue(SettingKey.ImportPathLidarr);
            try
            {
                await Task.Run(() => MatchingService.AutoMatchReleasesToArtists(QueueRecords.ToList(), Artists, importPath));
                // AutoMatch just populated MatchedArtist on records that didn't have one before -
                // queue up background prefetch for whichever artists that newly covers.
                EnqueueMatchedArtistPrefetches();
            }
            catch (Exception ex)
            {
                _status.ShowError($"Auto match failed: {ex.Message}");
            }
        });

        public Task OnQueueRecordSelectedAsync(LidarrQueueRecord? record) => RunBusyAsync("Loading files for selected release...", async () =>
        {
            SelectedQueueRecord = record;
            SelectedFile = null;
            CheckedFileIds.Clear();

            if (record == null)
            {
                ManualImportFiles.Clear();
                ArtistReleaseTracks.Clear();
                return;
            }

            try
            {
                // Usually already warm - PrefetchService fetches every queue record's files in
                // the background right after Refresh. On a cache miss (record just appeared, or
                // the background worker hasn't reached it yet) this fetches live and caches the
                // result, same as before.
                var files = await _prefetch.GetOrFetchQueueRecordFilesAsync(record);

                ManualImportFiles.Clear();
                foreach (var file in files)
                    ManualImportFiles.Add(file.Clone());

                foreach (var mf in ManualImportFiles)
                {
                    mf.ProposedActionType = null;
                    var pa = ProposedActions.FirstOrDefault(p => p.FileId == mf.Id);
                    if (pa != null) mf.ProposedActionType = pa.Action;
                }

                if (!string.IsNullOrWhiteSpace(record.MatchedArtist))
                {
                    await LoadArtistReleasesAsync(record.MatchedArtist);
                }
                else
                {
                    ArtistReleaseTracks.Clear();
                }
            }
            catch (Exception ex)
            {
                _status.ShowError($"Failed to load files: {ex.Message}");
            }
        });

        public async Task LoadArtistReleasesAsync(string artistName)
        {
            if (Artists.Count == 0) return;

            var artist = Artists.FirstOrDefault(a => MatchingService.Normalize(a.ArtistName) == MatchingService.Normalize(artistName));
            if (artist == null) return;

            ArtistReleaseTracks.Clear();

            try
            {
                // Same cache-or-fetch as above - warm whenever AutoMatch/manual match already
                // triggered a background prefetch for this artist.
                var tracks = await _prefetch.GetOrFetchArtistTracksAsync(artist);

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

        // Applied after a manual match is resolved (see ManualMatchDialog's result callback).
        public async Task ApplyManualMatchAsync(LidarrArtist selected)
        {
            if (SelectedQueueRecord == null) return;

            var already = Artists.Any(a => (a.Id != 0 && selected.Id != 0 && a.Id == selected.Id) ||
                string.Equals(MatchingService.Normalize(a.ArtistName), MatchingService.Normalize(selected.ArtistName), StringComparison.OrdinalIgnoreCase));
            if (!already)
                Artists.Add(selected);

            SelectedQueueRecord.Match = ReleaseMatchType.Exact;
            SelectedQueueRecord.MatchedArtist = selected.ArtistName;

            await RunBusyAsync("Applying manual match and loading artist releases...", async () =>
            {
                await LoadArtistReleasesAsync(SelectedQueueRecord.MatchedArtist);
            });
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

            var justAssigned = SelectedFile;
            MarkOtherFilesForUnlink(justAssigned, SelectedQueueRecord);

            ApplySort(SortMode);
            SelectNextFileInRelease(justAssigned);

            return MarkMatchResult.Success;
        }

        private void MarkOtherFilesForUnlink(LidarrManualImportFile assignedFile, LidarrQueueRecord? queueRecord)
        {
            if (queueRecord == null || string.IsNullOrWhiteSpace(queueRecord.Title))
                return;

            var originalRelease = queueRecord.Title;
            var assignedFolder = Path.GetDirectoryName(assignedFile.Path);

            foreach (var file in ManualImportFiles)
            {
                if (file.Id == assignedFile.Id) continue;
                if (file.ProposedActionType != null || AssignedFileIds.Contains(file.Id)) continue;
                if (ProposedActions.Any(p => p.FileId == file.Id)) continue;

                var fileFolder = Path.GetDirectoryName(file.Path);
                if (!string.Equals(fileFolder, assignedFolder, StringComparison.OrdinalIgnoreCase)) continue;

                var unlinkProposal = new ProposedAction
                {
                    Action = ProposalActionType.Unlink,
                    FileId = file.Id,
                    Path = file.Path,
                    OriginalFileName = Path.GetFileName(file.Path) ?? string.Empty,
                    OriginalRelease = originalRelease,
                    Quality = file.Quality
                };

                ProposedActions.Add(unlinkProposal);
                file.ProposedActionType = ProposalActionType.Unlink;
            }
        }

        private void SelectNextFileInRelease(LidarrManualImportFile currentFile)
        {
            var files = ManualImportFiles.ToList();
            var currentIndex = files.IndexOf(currentFile);

            if (currentIndex >= 0 && currentIndex < files.Count - 1)
            {
                SelectedFile = files[currentIndex + 1];
                return;
            }

            SelectedFile = files.FirstOrDefault(f => f.ProposedActionType != ProposalActionType.Import && !AssignedFileIds.Contains(f.Id));
        }

        #endregion

        #region Proposals

        public void CreateProposal(ProposalActionType kind, string? destinationName = null)
        {
            var selectedFiles = ManualImportFiles.Where(f => CheckedFileIds.Contains(f.Id)).ToList();
            if (selectedFiles.Count == 0 && SelectedFile != null)
                selectedFiles = new List<LidarrManualImportFile> { SelectedFile };

            if (selectedFiles.Count == 0)
            {
                _status.ShowError("Select one or more files from 'Unimported Release Files' first.");
                return;
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
        }

        public void MoveToDestination(ImportDestination dest)
        {
            var previouslySelected = SelectedFile;
            CreateProposal(ProposalActionType.MoveToDestination, dest.Name);

            if (previouslySelected != null)
                SelectNextFileInRelease(previouslySelected);
        }

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

            await RunBusyAsync("Importing files to Lidarr...", async () =>
            {
                var actionsSnapshot = ProposedActions.ToList();
                var prepare = _importRunner.PrepareImport(actionsSnapshot, new ImportResult());

                if (prepare.BackupFailed)
                {
                    // TryBackupFiles already wrote ImportStatus="Failed"/ErrorMessage directly
                    // onto the ProposedAction instances (actionsSnapshot shares references with
                    // ProposedActions) - just apply the usual cleanup pass and report everything
                    // as failed (nothing got far enough to import/move/copy).
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

            if (paused)
            {
                var missing = _coverArtGate.MissingCount;
                _status.SetInfo($"{missing} file(s) need cover art before import can continue - resolve them on the Cover Art page.");
            }
            else if (summary.HasAnyResult)
            {
                PostImportSummary(summary);
            }

            return summary;
        }

        // Called by the Cover Art page's Complete button once every item has been addressed (not
        // every file needs to end up with art - matching the WPF app, Complete is available even
        // if some are skipped).
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
