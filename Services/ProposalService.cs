using LidarrCompanion.Helpers;
using LidarrCompanion.Models;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System;

namespace LidarrCompanion.Services
{
    public class ProposalService
    {
        // New unified method to create a proposal action for NotForImport, Defer, Unlink, Delete
        public void CreateProposedAction(
            LidarrCompanion.Helpers.ProposalActionType actionType,
            //What we are linking
            LidarrManualImportFile selectedFile,
            LidarrQueueRecord? currentQueueRecord,
            //entries
            ObservableCollection<ProposedAction> proposedActions,
            ObservableCollection<LidarrManualImportFile> manualImportFiles,
            ObservableCollection<LidarrQueueRecord> queueRecords
            )
        {
                        
            // Remove any other proposals for this file (imports or related moves)
            var existingForFile = proposedActions.Where(p => p.FileId == selectedFile.Id).ToList();
            foreach (var ex in existingForFile) {
                // clear any file-level proposed action markers for the removed proposals
                var fileForEx = manualImportFiles.FirstOrDefault(f => f.Id == ex.FileId);
                if (fileForEx != null) fileForEx.ProposedActionType = null;
                proposedActions.Remove(ex);
            }
             

            // Remove any existing proposals for this file
            var existing = proposedActions.Where(p => p.FileId == selectedFile.Id).ToList();
            foreach (var e in existing) {
                var fileForE = manualImportFiles.FirstOrDefault(f => f.Id == e.FileId);
                if (fileForE != null) fileForE.ProposedActionType = null;
                proposedActions.Remove(e);
            }

            var originalRelease = currentQueueRecord is LidarrQueueRecord qr ? qr.Title : Path.GetFileName(selectedFile.Path) ?? string.Empty;

            var pa = new ProposedAction
            {
                OriginalFileName = selectedFile.Name,
                OriginalRelease = originalRelease,
                FileId = selectedFile.Id,
                Path = selectedFile.Path,
                DownloadId = currentQueueRecord?.DownloadId ?? string.Empty,
                Action = actionType
            };

            proposedActions.Add(pa);
            selectedFile.ProposedActionType = actionType;
            return;
            
            
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
            if ((selectedFile.ProposedActionType == ProposalActionType.Import) || assignedFileIds.Contains(selectedFile.Id))
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
                Action = LidarrCompanion.Helpers.ProposalActionType.Import
            };

            // Mark assigned bookkeeping: mark file as Import at file-level and track as assigned
            try
            {
                selectedFile.ProposedActionType = ProposalActionType.Import;
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
                                Action = LidarrCompanion.Helpers.ProposalActionType.NotForImport
                            };
                            proposedActions.Add(pa);
                            file.ProposedActionType = ProposalActionType.NotForImport;
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

                var existingMove = proposedActions.FirstOrDefault(x => x.FileId == p.FileId && x.Action == LidarrCompanion.Helpers.ProposalActionType.NotForImport);
                if (existingMove != null)
                {
                    var fileRow = manualImportFiles.FirstOrDefault(f => f.Id == existingMove.FileId);
                    if (fileRow != null) fileRow.ProposedActionType = null;
                    proposedActions.Remove(existingMove);
                }

                p.Action = LidarrCompanion.Helpers.ProposalActionType.Import;

                // Mark the manual file's ProposedActionType so the files list can reflect the proposal (Import shows transparent)
                if (matchedFile != null) matchedFile.ProposedActionType = p.Action;

                proposedActions.Add(p);
            }
        }
    }
}
