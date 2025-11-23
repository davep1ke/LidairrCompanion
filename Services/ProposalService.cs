using LidarrCompanion.Helpers;
using LidarrCompanion.Models;
using System.Collections.ObjectModel;
using System.IO;

namespace LidarrCompanion.Services
{
    public class ProposalService
    {
        // Mark a proposed action or selected file as NotForImport / move-to-not-selected
        public void MarkNotForImport(
        ProposedAction? selectedPa,
        LidarrManualImportFile? selectedFile,
        ObservableCollection<ProposedAction> proposedActions,
        ObservableCollection<LidarrManualImportFile> manualImportFiles,
        ObservableCollection<LidarrQueueRecord> queueRecords,
        LidarrQueueRecord? currentQueueRecord)
        {
            // If a proposed action is selected, mark it as NotForImport and flag underlying file
            if (selectedPa != null)
            {
                // Remove any other proposals for this file (imports or move/defer)
                var existingForFile = proposedActions.Where(p => p.FileId == selectedPa.FileId && p != selectedPa).ToList();
                foreach (var ex in existingForFile)
                {
                    proposedActions.Remove(ex);
                }

                selectedPa.IsNotForImport = true;
                selectedPa.IsMoveToNotSelected = false;
                selectedPa.ActionType = "Not For Import";

                var fileRow = manualImportFiles.FirstOrDefault(f => f.Id == selectedPa.FileId);
                if (fileRow != null)
                {
                    fileRow.IsMarkedNotSelected = true;
                }

                // Mark the parent queue record (if any) as having a not-selected file
                LidarrQueueRecord? queueRecord = currentQueueRecord;
                if (queueRecord == null && selectedPa != null)
                {
                    queueRecord = queueRecords.FirstOrDefault(q => string.Equals(q.Title, selectedPa.OriginalRelease, StringComparison.OrdinalIgnoreCase) || string.Equals(q.DownloadId, selectedPa.DownloadId, StringComparison.OrdinalIgnoreCase));
                }
                if (queueRecord != null)
                {
                    queueRecord.HasNotSelectedMarked = true;
                }

                return;
            }

            // Otherwise if a file row is selected, create a move-to-not-selected proposed action
            if (selectedFile != null)
            {
                // Remove any existing proposals for this file (import/defer/move)
                var existing = proposedActions.Where(p => p.FileId == selectedFile.Id).ToList();
                foreach (var e in existing) proposedActions.Remove(e);

                var notSelectedRoot = AppSettings.GetValue(SettingKey.NotSelectedPath);
                var originalRelease = currentQueueRecord is LidarrQueueRecord qr ? qr.Title : Path.GetFileName(selectedFile.Path) ?? string.Empty;

                var movePa = new ProposedAction
                {
                    OriginalFileName = selectedFile.Name,
                    OriginalRelease = originalRelease,
                    FileId = selectedFile.Id,
                    Path = selectedFile.Path,
                    IsMoveToNotSelected = true,
                    MoveDestinationPath = string.IsNullOrWhiteSpace(notSelectedRoot) ? string.Empty : Path.Combine(notSelectedRoot, originalRelease),
                    ActionType = "Move to Not Import"
                };

                proposedActions.Add(movePa);
                selectedFile.IsMarkedNotSelected = true;

                // mark queue record as having not-selected file
                var qr2 = currentQueueRecord ?? queueRecords.FirstOrDefault(q => string.Equals(q.Title, originalRelease, StringComparison.OrdinalIgnoreCase));
                if (qr2 != null) qr2.HasNotSelectedMarked = true;
            }
        }

        // New: Mark proposed action or selected file as deferred (move to DeferDestinationPath)
        public void MarkDeferred(
            ProposedAction? selectedPa,
            LidarrManualImportFile? selectedFile,
            ObservableCollection<ProposedAction> proposedActions,
            ObservableCollection<LidarrManualImportFile> manualImportFiles,
            ObservableCollection<LidarrQueueRecord> queueRecords,
            LidarrQueueRecord? currentQueueRecord)
        {
            // If a proposed action is selected, mark it as deferred and flag underlying file
            if (selectedPa != null)
            {
                // Remove any other proposals for this file (imports or not-for-import)
                var existingForFile = proposedActions.Where(p => p.FileId == selectedPa.FileId && p != selectedPa).ToList();
                foreach (var ex in existingForFile)
                {
                    proposedActions.Remove(ex);
                }

                selectedPa.IsNotForImport = false;
                selectedPa.IsMoveToNotSelected = false; // treat Defer as separate action type (actionType string used)
                selectedPa.ActionType = "Defer";

                var fileRow = manualImportFiles.FirstOrDefault(f => f.Id == selectedPa.FileId);
                if (fileRow != null)
                {
                    // Use IsMarkedNotSelected for visual flag reuse; keep it true so UI indicates it's been handled
                    fileRow.IsMarkedNotSelected = true;
                }

                // Mark the parent queue record (if any) as having a deferred file
                LidarrQueueRecord? queueRecord = currentQueueRecord;
                if (queueRecord == null && selectedPa != null)
                {
                    queueRecord = queueRecords.FirstOrDefault(q => string.Equals(q.Title, selectedPa.OriginalRelease, StringComparison.OrdinalIgnoreCase) || string.Equals(q.DownloadId, selectedPa.DownloadId, StringComparison.OrdinalIgnoreCase));
                }
                if (queueRecord != null)
                {
                    queueRecord.HasNotSelectedMarked = true; // reuse flag
                }

                return;
            }

            // Otherwise if a file row is selected, create a move-to-defer proposed action
            if (selectedFile != null)
            {
                // Remove any existing proposals for this file (import/not-for-import/move)
                var existing = proposedActions.Where(p => p.FileId == selectedFile.Id).ToList();
                foreach (var e in existing) proposedActions.Remove(e);

                var deferRoot = AppSettings.GetValue(SettingKey.DeferDestinationPath);
                var originalRelease = currentQueueRecord is LidarrQueueRecord qr ? qr.Title : Path.GetFileName(selectedFile.Path) ?? string.Empty;

                var movePa = new ProposedAction
                {
                    OriginalFileName = selectedFile.Name,
                    OriginalRelease = originalRelease,
                    FileId = selectedFile.Id,
                    Path = selectedFile.Path,
                    IsMoveToNotSelected = true, // reuse move flag so ImportService will move before import
                    MoveDestinationPath = string.IsNullOrWhiteSpace(deferRoot) ? string.Empty : Path.Combine(deferRoot, originalRelease),
                    ActionType = "Defer"
                };

                proposedActions.Add(movePa);
                selectedFile.IsMarkedNotSelected = true;

                // mark queue record as having deferred file
                var qr2 = currentQueueRecord ?? queueRecords.FirstOrDefault(q => string.Equals(q.Title, originalRelease, StringComparison.OrdinalIgnoreCase));
                if (qr2 != null) qr2.HasNotSelectedMarked = true;
            }
        }

        // Create a manual assignment proposal and sibling move-to-not-selected proposals
        public ProposedAction CreateManualAssignment(
        LidarrManualImportFile selectedFile,
        LidarrArtistReleaseTrack selectedTrack,
        LidarrQueueRecord? selectedQueueRecord,
        List<LidarrArtist> artists,
        ObservableCollection<ProposedAction> proposedActions,
        ObservableCollection<LidarrManualImportFile> manualImportFiles,
        HashSet<int> assignedFileIds,
        HashSet<int> assignedTrackIds)
        {
            if (selectedFile == null || selectedTrack == null) throw new ArgumentNullException();

            // Prevent re-assignment
            if (selectedFile.IsAssigned || assignedFileIds.Contains(selectedFile.Id))
                throw new InvalidOperationException("This file has already been assigned.");
            if (selectedTrack.IsAssigned || assignedTrackIds.Contains(selectedTrack.TrackId))
                throw new InvalidOperationException("This track has already been assigned.");

            var originalRelease = selectedQueueRecord?.Title ?? Path.GetFileName(selectedFile.Path) ?? string.Empty;

            var newAction = new ProposedAction
            {
                OriginalFileName = selectedFile.Name,
                OriginalRelease = originalRelease,
                MatchedArtist = selectedQueueRecord?.MatchedArtist ?? string.Empty,
                MatchedTrack = selectedTrack.Track,
                MatchedRelease = selectedTrack.Release,
                TrackId = selectedTrack.TrackId,
                FileId = selectedFile.Id,
                ArtistId = artists.FirstOrDefault(a => MatchingService.Normalize(a.ArtistName) == MatchingService.Normalize(selectedQueueRecord?.MatchedArtist ?? string.Empty))?.Id ?? 0,
                AlbumId = selectedTrack.AlbumId,
                AlbumReleaseId = selectedTrack.ReleaseId,
                Path = selectedFile.Path,
                DownloadId = selectedQueueRecord?.DownloadId ?? string.Empty,
                Quality = selectedFile.Quality,
                ActionType = "Import"
            };

            // Mark assigned bookkeeping
            try
            {
                selectedFile.IsAssigned = true;
                selectedTrack.IsAssigned = true;
                assignedFileIds.Add(selectedFile.Id);
                assignedTrackIds.Add(selectedTrack.TrackId);
            }
            catch
            {
                // ignore any set failures - this should not happen
            }

            // Remove any existing proposal for this file before adding
            var existingForSelected = proposedActions.FirstOrDefault(p => p.FileId == selectedFile.Id);
            if (existingForSelected != null) proposedActions.Remove(existingForSelected);

            proposedActions.Add(newAction);

            // Create move-to-not-selected proposals for other files in same folder
            try
            {
                var notSelectedRoot = LidarrCompanion.Models.AppSettings.GetValue(LidarrCompanion.Models.SettingKey.NotSelectedPath);
                var selectedParent = Path.GetDirectoryName(selectedFile.Path) ?? string.Empty;
                foreach (var file in manualImportFiles)
                {
                    if (file.Id == selectedFile.Id) continue;
                    var otherParent = Path.GetDirectoryName(file.Path) ?? string.Empty;
                    if (string.Equals(selectedParent, otherParent, StringComparison.OrdinalIgnoreCase))
                    {
                        var existing = proposedActions.FirstOrDefault(a => a.FileId == file.Id);
                        if (existing == null)
                        {
                            var pa = new ProposedAction
                            {
                                OriginalFileName = file.Name,
                                OriginalRelease = originalRelease,
                                FileId = file.Id,
                                Path = file.Path,
                                IsMoveToNotSelected = true,
                                MoveDestinationPath = string.IsNullOrWhiteSpace(notSelectedRoot) ? string.Empty : Path.Combine(notSelectedRoot, originalRelease),
                                ActionType = "Move to Not Import"
                            };
                            proposedActions.Add(pa);
                            file.IsMarkedNotSelected = true;
                        }
                    }
                }
            }
            catch
            {
                // ignore
            }

            return newAction;
        }

        // Populate and add AI returned proposals to UI collections
        public void ApplyAiProposals(
        IEnumerable<ProposedAction> proposed,
        ObservableCollection<LidarrArtistReleaseTrack> artistReleaseTracks,
        ObservableCollection<LidarrManualImportFile> manualImportFiles,
        List<LidarrArtist> artists,
        ObservableCollection<ProposedAction> proposedActions,
        LidarrQueueRecord selectedRecord,
        HashSet<int> assignedFileIds,
        HashSet<int> assignedTrackIds)
        {
            foreach (var p in proposed)
            {
                var trackItem = artistReleaseTracks.FirstOrDefault(t => t.TrackId == p.TrackId);
                if (trackItem != null)
                {
                    p.AlbumReleaseId = trackItem.ReleaseId;
                    p.AlbumId = trackItem.AlbumId;

                    var artistPath = artists.FirstOrDefault(a => MatchingService.Normalize(a.ArtistName) == MatchingService.Normalize(p.MatchedArtist))?.Path ?? string.Empty;
                    var fallbackPath = string.Empty;
                    if (!string.IsNullOrWhiteSpace(artistPath) && !string.IsNullOrWhiteSpace(trackItem.Release))
                    {
                        try { fallbackPath = System.IO.Path.Combine(artistPath, trackItem.Release); } catch { fallbackPath = artistPath; }
                    }
                    else if (!string.IsNullOrWhiteSpace(artistPath))
                    {
                        fallbackPath = artistPath;
                    }
                    p.Path = !string.IsNullOrWhiteSpace(trackItem.ReleasePath) ? trackItem.ReleasePath : (!string.IsNullOrWhiteSpace(trackItem.AlbumPath) ? trackItem.AlbumPath : fallbackPath);
                }

                if (!string.IsNullOrWhiteSpace(p.MatchedArtist))
                {
                    var art = artists.FirstOrDefault(a => MatchingService.Normalize(a.ArtistName) == MatchingService.Normalize(p.MatchedArtist));
                    if (art != null) p.ArtistId = art.Id;
                }

                p.DownloadId = selectedRecord?.DownloadId ?? string.Empty;

                var matchedFile = manualImportFiles.FirstOrDefault(f => f.Id == p.FileId);
                if (matchedFile != null) p.Quality = matchedFile.Quality;

                var existingMove = proposedActions.FirstOrDefault(x => x.FileId == p.FileId && x.IsMoveToNotSelected);
                if (existingMove != null)
                {
                    var fileRow = manualImportFiles.FirstOrDefault(f => f.Id == existingMove.FileId);
                    if (fileRow != null) fileRow.IsMarkedNotSelected = false;
                    proposedActions.Remove(existingMove);
                }

                p.ActionType = "Import";

                proposedActions.Add(p);
            }
        }
    }
}
