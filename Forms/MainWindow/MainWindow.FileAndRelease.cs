using LidarrCompanion.Helpers;
using LidarrCompanion.Models;
using LidarrCompanion.Services;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Threading.Tasks;
using System.Linq;
using System.IO;
using System.Diagnostics;
using System.Windows.Input;
using System;

namespace LidarrCompanion
{
    public partial class MainWindow
    {
       
        // Generic creator for frontend proposals (NotForImport, Defer, Unlink, Delete, or dynamic destination)
        private void CreateProposal(LidarrCompanion.Helpers.ProposalActionType kind, string? destinationName = null)
        {
            try
            {
                var currentQueueRecord = list_QueueRecords.SelectedItem as LidarrQueueRecord;
               
                // Otherwise operate on selected files
                var selectedFiles = list_Files_in_Release.SelectedItems.Cast<object>().OfType<LidarrManualImportFile>().ToList();
                if (selectedFiles == null || selectedFiles.Count == 0)
                {
                    MessageBox.Show("Select one or more files from 'Unimported Release Files' first.", "No file selected", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                foreach (var selFile in selectedFiles)
                {
                    // Remove any existing proposals for this file
                    var existingForFile = _proposedActions.Where(p => p.FileId == selFile.Id).ToList();
                    foreach (var ex in existingForFile)
                    {
                        var fileForEx = _manualImportFiles.FirstOrDefault(f => f.Id == ex.FileId);
                        if (fileForEx != null) fileForEx.ProposedActionType = null;
                        _proposedActions.Remove(ex);
                    }

                    var originalRelease = currentQueueRecord is LidarrQueueRecord qr ? qr.Title : Path.GetFileName(selFile.Path) ?? string.Empty;

                    var pa = new ProposedAction
                    {
                        OriginalFileName = selFile.Name,
                        OriginalRelease = originalRelease,
                        FileId = selFile.Id,
                        Path = selFile.Path,
                        DownloadId = currentQueueRecord?.DownloadId ?? string.Empty,
                        Action = kind,
                        DestinationName = destinationName ?? string.Empty
                    };

                    _proposedActions.Add(pa);
                    selFile.ProposedActionType = kind;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Operation failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Populate the individual destination buttons (called when destinations change)
        private void PopulateDestinationButtons()
        {
            var destinations = AppSettings.Current.ImportDestinations ?? new List<ImportDestination>();
            ic_DestinationButtons.ItemsSource = destinations;
            
            Logger.Log($"Populated {destinations.Count} destination buttons", LogSeverity.Verbose, new { Count = destinations.Count });
        }

        // Handler for clicking an individual "Move To" destination button
        private void btn_MoveToDestination_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is Button button && button.Tag is ImportDestination dest)
                {
                    CreateProposal(ProposalActionType.MoveToDestination, dest.Name);
                    
                    // Auto-select next file after creating proposal
                    if (list_Files_in_Release.SelectedItem is LidarrManualImportFile selectedFile)
                    {
                        SelectNextFileInRelease(selectedFile);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to create move proposal: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Handler for clicking an individual "Open Folder" destination button
        private void btn_OpenFolderDestination_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is Button button && button.Tag is ImportDestination dest)
                {
                    if (string.IsNullOrWhiteSpace(dest.DestinationPath) || !Directory.Exists(dest.DestinationPath))
                    {
                        MessageBox.Show($"Destination folder '{dest.Name}' is not configured or does not exist.", "Folder Missing", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    Process.Start(new ProcessStartInfo("explorer.exe", dest.DestinationPath) { UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to open folder: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void btn_AI_SearchMatch_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;

            if (list_QueueRecords.SelectedItem is not LidarrQueueRecord selectedRecord)
            {
                MessageBox.Show("Select a release from the list first.", "No selection", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (string.IsNullOrWhiteSpace(selectedRecord.MatchedArtist))
            {
                MessageBox.Show("Selected release has no matched artist. Use Auto or Manual match first.", "No Artist", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var candidateFiles = _manualImportFiles.Where(f => (f.ProposedActionType != ProposalActionType.Import) && !_assignedFileIds.Contains(f.Id)).ToList();
            if (!candidateFiles.Any())
            {
                MessageBox.Show("No unassigned files available to match.", "Nothing to match", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var (albums, tracksByReleaseId) = MatchingService.BuildAlbumsFromUiCollections(_manualImportFiles, _artistReleaseTracks, selectedRecord.MatchedArtist);

            SetBusy("Running AI matching...");
            try
            {
                var proposed = await MatchingService.AiMatchAsync(candidateFiles, _artistReleaseTracks, albums, tracksByReleaseId, selectedRecord.MatchedArtist, selectedRecord, _assignedFileIds, _assignedTrackIds);

                if (proposed == null || !proposed.Any())
                {
                    MessageBox.Show("No confident matches returned by the AI.", "No Matches", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // Ensure each ProposedAction has cached ArtistId/AlbumId/AlbumReleaseId using UI data
                // Delegate applying proposals to ProposalService
                _proposalService.ApplyAiProposals(proposed, _artistReleaseTracks, _manualImportFiles, _artists, _proposedActions, selectedRecord, _assignedFileIds, _assignedTrackIds);

                if (!_proposedActions.Any())
                {
                    MessageBox.Show("No confident matches could be applied (all matches were skipped or duplicates).", "No Applied Matches", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"AI match failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                ClearBusy();
            }
        }


        private async void btn_MarkMatch_Click(object sender, RoutedEventArgs e)
        {
            // If multiple files selected, this action is not supported
            if (list_Files_in_Release.SelectedItems != null && list_Files_in_Release.SelectedItems.Count > 1)
            {
                MessageBox.Show("Mark Match requires a single file selection. Select only one file to mark.", "Multiple selection", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (list_Files_in_Release.SelectedItem is not LidarrManualImportFile selectedFile)
            {
                MessageBox.Show("Select a file from 'Unimported Release Files' first.", "No file selected", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (list_Artist_Releases.SelectedItem is not LidarrArtistReleaseTrack selectedTrack)
            {
                MessageBox.Show("Select a track from 'Artist Release Files' first.", "No track selected", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Prevent re-assignment
            if ((selectedFile.ProposedActionType == ProposalActionType.Import) || _assignedFileIds.Contains(selectedFile.Id))
            {
                MessageBox.Show("This file has already been assigned.", "Already assigned", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (selectedTrack.IsAssigned || _assignedTrackIds.Contains(selectedTrack.TrackId))
            {
                MessageBox.Show("This track has already been assigned.", "Already assigned", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // If track already has a file on disk, confirm overwrite
            if (selectedTrack.HasFile)
            {
                var confirmOverwrite = MessageBox.Show("Selected track already has a file. Assigning will overwrite it. Continue?", "Overwrite Confirmation", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (confirmOverwrite != MessageBoxResult.Yes)
                    return;
            }

            // Build proposed action (originalRelease: try selected queue record title if available)
            var selectedQueueRecord = list_QueueRecords.SelectedItem as LidarrQueueRecord;
            var originalRelease = selectedQueueRecord?.Title ?? Path.GetFileName(selectedFile.Path) ?? string.Empty;

            // compute path fallback: prefer release path, then album path, then artist path + release (fallback to empty)
            var artistPath = _artists.FirstOrDefault(a => MatchingService.Normalize(a.ArtistName) == MatchingService.Normalize(selectedQueueRecord?.MatchedArtist ?? string.Empty))?.Path ?? string.Empty;
            var fallbackPath = string.Empty;
            if (!string.IsNullOrWhiteSpace(artistPath) && !string.IsNullOrWhiteSpace(selectedTrack.Release))
            {
                // join artist path and release name
                try { fallbackPath = System.IO.Path.Combine(artistPath, selectedTrack.Release); } catch { fallbackPath = artistPath; }
            }
            else if (!string.IsNullOrWhiteSpace(artistPath))
            {
                fallbackPath = artistPath;
            }

            // Use ProposalService to create and add proposals
            _proposalService.CreateManualAssignment(selectedFile, selectedTrack, selectedQueueRecord, _artists, _proposedActions, _manualImportFiles, _assignedFileIds, _assignedTrackIds);

            // Mark assigned (file-level ProposedActionType is set by proposal service)
            selectedFile.ProposedActionType = ProposalActionType.Import;
            selectedTrack.IsAssigned = true;
            _assignedFileIds.Add(selectedFile.Id);
            _assignedTrackIds.Add(selectedTrack.TrackId);

            // Mark all other files from the same release for Unlink
            MarkOtherFilesForUnlink(selectedFile, selectedQueueRecord);

            // Recompute scores because assignment state changed
            if (cmb_SortMode.SelectedItem is ComboBoxItem item)
            {
                ApplyArtistReleasesSort(item.Content?.ToString() ?? string.Empty);
            }

            // Auto-select next file in the list
            SelectNextFileInRelease(selectedFile);
        }

        private void MarkOtherFilesForUnlink(LidarrManualImportFile assignedFile, LidarrQueueRecord? queueRecord)
        {
            try
            {
                if (queueRecord == null)
                {
                    Logger.Log("Cannot mark other files for unlink - no queue record", LogSeverity.Verbose);
                    return;
                }

                var originalRelease = queueRecord.Title ?? string.Empty;
                if (string.IsNullOrWhiteSpace(originalRelease))
                {
                    Logger.Log("Cannot mark other files for unlink - no release title", LogSeverity.Verbose);
                    return;
                }

                Logger.Log($"Marking other files in release '{originalRelease}' for Unlink", LogSeverity.Low, new { Release = originalRelease, AssignedFileId = assignedFile.Id });

                int markedCount = 0;
                foreach (var file in _manualImportFiles)
                {
                    // Skip the file we just assigned
                    if (file.Id == assignedFile.Id)
                        continue;

                    // Skip files that already have ANY proposed action (don't overwrite existing matches)
                    if (file.ProposedActionType != null || _assignedFileIds.Contains(file.Id))
                        continue;

                    // Check if this file already has any proposal
                    var existingProposal = _proposedActions.FirstOrDefault(p => p.FileId == file.Id);
                    if (existingProposal != null)
                        continue;

                    // Check if this file is from the same release (same queue record)
                    var fileFolder = Path.GetDirectoryName(file.Path);
                    var assignedFolder = Path.GetDirectoryName(assignedFile.Path);
                    
                    if (string.Equals(fileFolder, assignedFolder, StringComparison.OrdinalIgnoreCase))
                    {
                        // Create Unlink proposal for this file
                        var existingUnlinkProposal = _proposedActions.FirstOrDefault(p => p.FileId == file.Id && p.Action == ProposalActionType.Unlink);
                        
                        if (existingUnlinkProposal == null)
                        {
                            var unlinkProposal = new ProposedAction
                            {
                                Action = ProposalActionType.Unlink,
                                FileId = file.Id,
                                Path = file.Path,
                                OriginalFileName = Path.GetFileName(file.Path) ?? string.Empty,
                                OriginalRelease = originalRelease,
                                Quality = file.Quality
                            };

                            _proposedActions.Add(unlinkProposal);
                            file.ProposedActionType = ProposalActionType.Unlink;
                            markedCount++;
                            
                            Logger.Log($"Marked file for Unlink: {unlinkProposal.OriginalFileName}", LogSeverity.Verbose, new { FileId = file.Id, FileName = unlinkProposal.OriginalFileName }, filePath: file.Path);
                        }
                    }
                }

                Logger.Log($"Marked {markedCount} other file(s) for Unlink in release '{originalRelease}'", LogSeverity.Low, new { MarkedCount = markedCount, Release = originalRelease });
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to mark other files for unlink: {ex.Message}", LogSeverity.Medium, new { Error = ex.Message });
            }
        }

        private void SelectNextFileInRelease(LidarrManualImportFile currentFile)
        {
            try
            {
                var currentIndex = list_Files_in_Release.Items.IndexOf(currentFile);
                if (currentIndex >= 0 && currentIndex < list_Files_in_Release.Items.Count - 1)
                {
                    // Select the next file
                    list_Files_in_Release.SelectedIndex = currentIndex + 1;
                    list_Files_in_Release.ScrollIntoView(list_Files_in_Release.SelectedItem);
                }
                else if (list_Files_in_Release.Items.Count > 0)
                {
                    // If we were at the last item, stay there or go to first unassigned
                    for (int i = 0; i < list_Files_in_Release.Items.Count; i++)
                    {
                        if (list_Files_in_Release.Items[i] is LidarrManualImportFile file && 
                            file.ProposedActionType != ProposalActionType.Import &&
                            !_assignedFileIds.Contains(file.Id))
                        {
                            list_Files_in_Release.SelectedIndex = i;
                            list_Files_in_Release.ScrollIntoView(list_Files_in_Release.SelectedItem);
                            return;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to auto-select next file: {ex.Message}", LogSeverity.Low, new { Error = ex.Message });
            }
        }
        

        private async Task LoadArtistReleasesAsync(string artistName)
        {
            if (_artists == null || _artists.Count == 0)
                return;

            // Find the artist id (normalize for robustness)
            var artist = _artists.FirstOrDefault(a => MatchingService.Normalize(a.ArtistName) == MatchingService.Normalize(artistName));
            if (artist == null)
                return;

            _artistReleaseTracks.Clear();

            try
            {
                var albums = await MatchingService.GetAlbumsForArtistAsync(artist.Id);


                // Re-run the original population logic using LidarrHelper directly for tracks
                var lidarr = new LidarrHelper();
                foreach (var album in albums)
                {
                    foreach (var release in album.Releases)
                    {
                        var tracks = await lidarr.GetTracksByReleaseAsync(release.Id);

                        var format = string.IsNullOrWhiteSpace(release.Format) ? string.Empty : release.Format;
                        var country = string.IsNullOrWhiteSpace(release.Country) ? string.Empty : release.Country;

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
                                AlbumId = album.Id,
                                AlbumPath = album.Path ?? string.Empty,
                                ReleasePath = release.Path ?? string.Empty,
                                IsAssigned = _assignedTrackIds.Contains(track.Id),
                                AlbumType = album.AlbumType ?? string.Empty
                            };

                            _artistReleaseTracks.Add(tr);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading artist releases: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void cmb_SortMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cmb_SortMode.SelectedItem is ComboBoxItem item)
            {
                var mode = item.Content?.ToString() ?? string.Empty;
                ApplyArtistReleasesSort(mode);
            }
        }

        private void ApplyArtistReleasesSort(string mode)
        {
            IEnumerable<LidarrArtistReleaseTrack> items = _artistReleaseTracks.ToList();

            switch (mode)
            {
                case "by Release":
                    // reset scores
                    foreach (var it in _artistReleaseTracks) it.Score = 0.0;
                    items = items.OrderBy(i => i.Release).ThenBy(i => i.Track);
                    break;
                case "by Track Num":
                    foreach (var it in _artistReleaseTracks) it.Score = 0.0;
                    items = items.OrderBy(i => MatchingService.ParseTrackNumber(i.Track)).ThenBy(i => i.Track);
                    break;
                case "by Track Name":
                    foreach (var it in _artistReleaseTracks) it.Score = 0.0;
                    items = items.OrderBy(i => MatchingService.StripTrackNumberPrefix(i.Track)).ThenBy(i => i.Track);
                    break;
                case "by Best":
                    // If nothing selected in files list, fallback to release sort
                    if (!(list_Files_in_Release.SelectedItem is LidarrManualImportFile selectedFile))
                    {
                        foreach (var it in _artistReleaseTracks) it.Score = 0.0;
                        items = items.OrderBy(i => i.Release).ThenBy(i => i.Track);
                        break;
                    }

                    var selFileName = selectedFile.Name;
                    var selFileId = selectedFile.Id;

                    // attempt to read selected record's matched artist for better scoring
                    var selectedQueueRecord = list_QueueRecords.SelectedItem as LidarrQueueRecord;
                    var selectedArtistName = selectedQueueRecord?.MatchedArtist ?? string.Empty;

                    // read boost from settings (default10)
                    int releaseBoost = AppSettings.Current.GetTyped<int>(SettingKey.ReleaseBoost);
                    if (releaseBoost <= 0) releaseBoost = 10;

                    // Compute score per-track and assign to the Score property then order by Score desc
                    foreach (var i in _artistReleaseTracks)
                    {
                        double baseScore = MatchingService.ComputeMatchScore(selFileName, i, selectedArtistName);

                        // Do not apply release-level boost if the track already has a file or the selected file is already assigned
                        if (i.HasFile || selectedFile.ProposedActionType == ProposalActionType.Import)
                        {
                            i.Score = baseScore;
                            continue;
                        }

                        // If the selected file is already part of a proposed action for this release, do not boost
                        var selectedFileAlreadyMappedToThisRelease = _proposedActions.Any(a => a.FileId == selFileId && a.AlbumReleaseId == i.ReleaseId);
                        if (selectedFileAlreadyMappedToThisRelease)
                        {
                            i.Score = baseScore;
                            continue;
                        }

                        // Check if any other track in the same release is assigned (either via IsAssigned on track rows or via assigned ID set or proposed actions with different file)
                        bool otherAssignedInRelease = _artistReleaseTracks.Any(t => t.ReleaseId == i.ReleaseId && t.IsAssigned && t.TrackId != i.TrackId)
                            || _assignedTrackIds.Any(id => _artistReleaseTracks.Any(t => t.ReleaseId == i.ReleaseId && t.TrackId == id && t.TrackId != i.TrackId))
                            || _proposedActions.Any(a => a.AlbumReleaseId == i.ReleaseId && a.FileId != selFileId);

                        if (otherAssignedInRelease)
                            i.Score = baseScore + releaseBoost;
                        else
                            i.Score = baseScore;
                    }

                    items = items.OrderByDescending(i => i.Score).ThenBy(i => i.Release).ThenBy(i => i.Track);
                    break;
                default:
                    foreach (var it in _artistReleaseTracks) it.Score = 0.0;
                    items = items.OrderBy(i => i.Release).ThenBy(i => i.Track);
                    break;
            }

            // Re-populate the observable collection preserving selection
            var selected = list_Artist_Releases.SelectedItem as LidarrArtistReleaseTrack;
            _artistReleaseTracks.Clear();
            foreach (var it in items)
                _artistReleaseTracks.Add(it);

            if (selected != null)
            {
                var toSelect = _artistReleaseTracks.FirstOrDefault(t => t.TrackId == selected.TrackId && t.ReleaseId == selected.ReleaseId);
                if (toSelect != null) list_Artist_Releases.SelectedItem = toSelect;
            }

            // Ensure list scrolls to top after re-ordering (so best matches are immediately visible)
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_artistReleaseTracks.Count > 0)
                    list_Artist_Releases.ScrollIntoView(_artistReleaseTracks[0]);
            }), System.Windows.Threading.DispatcherPriority.Background);

            // Update sibling-release highlighting after any reorder/populate
            UpdateReleaseAssignmentHighlights();
        }

        // Update IsReleaseHasAssigned on artist release rows so the UI can highlight
        // other tracks in a release when some tracks are matched/assigned.
        private void UpdateReleaseAssignmentHighlights()
        {
            try
            {
                // Determine which releases have any assigned tracks or proposed actions (non-move)
                var releasesWithAssigned = new HashSet<int>(_artistReleaseTracks.Where(t => t.IsAssigned).Select(t => t.ReleaseId));

                // Include releases that have proposed actions targeting that release (and not move-to-not-selected / not-for-import)
                foreach (var pa in _proposedActions)
                {
                    if (pa.Action == LidarrCompanion.Helpers.ProposalActionType.Import && pa.AlbumReleaseId != 0)
                        releasesWithAssigned.Add(pa.AlbumReleaseId);
                }

                // Now set the flag on each track: true when the release has assigned tracks but this track itself is not assigned
                foreach (var tr in _artistReleaseTracks)
                {
                    tr.ReleaseHasOtherAssigned = releasesWithAssigned.Contains(tr.ReleaseId) && !tr.IsAssigned;
                }
            }
            catch
            {
                // ignore
            }
        }

        private void btn_NotForImport_Click(object sender, RoutedEventArgs e)
        {
            CreateProposal(ProposalActionType.NotForImport);
        }

        // Defer import handler: create a move-to-defer proposal or mark selected ProposedAction deferred
        private void btn_DeferImport_Click(object sender, RoutedEventArgs e)
        {
            CreateProposal(ProposalActionType.Defer);
        }


        private void btn_UnlinkFile_Click(object sender, RoutedEventArgs e)
        {
            CreateProposal(ProposalActionType.Unlink);
        }

        private void btn_DeleteFile_Click(object sender, RoutedEventArgs e)
        {
            CreateProposal(ProposalActionType.Delete);
        }



        private void btn_PlayTrack_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (list_Files_in_Release.SelectedItem is not LidarrManualImportFile selectedFile)
                {
                    MessageBox.Show("Select a file from 'Unimported Release Files' first.", "No file selected", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var serverImportPath = AppSettings.GetValue(SettingKey.LidarrImportPath);
                var localMapping = AppSettings.GetValue(SettingKey.LidarrImportPathLocal);

                // Toggle play/stop
                if (_audioPlayer != null && _audioPlayer.IsPlaying)
                {
                    try
                    {
                        _audioPlayer.Stop();
                    }
                    finally
                    {
                        _audioPlayer.Dispose();
                        _audioPlayer = null;
                        btn_PlayTrack.Content = "Play Track";
                    }
                    return;
                }

                _audioPlayer = new FileAndAudioService();
                try
                {
                    _audioPlayer.PlayMapped(selectedFile.Path ?? string.Empty, serverImportPath, localMapping);
                    btn_PlayTrack.Content = "Stop";
                }
                catch (InvalidOperationException ex) when (ex.InnerException is System.Runtime.InteropServices.SEHException)
                {
                    // SEHException from native audio components - already logged in FileAndAudioService
                    MessageBox.Show($"Audio playback failed due to a codec or driver issue.\n\n{ex.Message}", "Playback Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    _audioPlayer.Dispose();
                    _audioPlayer = null;
                    btn_PlayTrack.Content = "Play Track";
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to play file: {ex.Message}", "Playback error", MessageBoxButton.OK, MessageBoxImage.Error);
                    _audioPlayer.Dispose();
                    _audioPlayer = null;
                    btn_PlayTrack.Content = "Play Track";
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Playback error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Right-click handler to advance audio by30 seconds
        private void btn_PlayTrack_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (_audioPlayer != null && _audioPlayer.IsPlaying)
                {
                    _audioPlayer.AdvanceBy(TimeSpan.FromSeconds(30));
                }
            }
            catch
            {
                // ignore
            }
        }

        private void btn_OpenReleaseFolder_Click(object sender, RoutedEventArgs e)
        {
            var serverImportPath = AppSettings.GetValue(SettingKey.LidarrImportPath);
            var localMapping = AppSettings.GetValue(SettingKey.LidarrImportPathLocal);

            // If a file is selected in the Files-in-Release list, open its containing folder
            if (list_Files_in_Release.SelectedItem is LidarrManualImportFile selectedFile)
            {
                try
                {
                    var folder = FileAndAudioService.OpenContainingFolder(selectedFile.Path ?? string.Empty, serverImportPath, localMapping);
                    txt_Status.Text = folder;
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to open folder: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }

                return;
            }

            // If no file is selected, prefer the currently selected item in the CurrentFiles list (queue record)
            if (list_QueueRecords.SelectedItem != null)
            {
                // Try to resolve OutputPath property via dynamic cast to expected type (LidarrQueueRecord)
                var queueItem = list_QueueRecords.SelectedItem as dynamic;
                string? outputPath = null;
                try
                {
                    outputPath = queueItem?.OutputPath as string;
                }
                catch
                {
                    outputPath = null;
                }

                if (!string.IsNullOrWhiteSpace(outputPath))
                {
                    try
                    {
                        var folder = FileAndAudioService.OpenContainingFolder(outputPath, serverImportPath, localMapping);
                        txt_Status.Text = folder;
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Failed to open folder: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }

                    return;
                }
            }            
        }

        private void list_Files_in_Release_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            try
            {
                if (cmb_SortMode.SelectedItem is ComboBoxItem item)
                {
                    var mode = item.Content?.ToString() ?? string.Empty;
                    ApplyArtistReleasesSort(mode);
                }
                else
                {
                    ApplyArtistReleasesSort(string.Empty);
                }
            }
            catch
            {
                // ignore
            }
        }





    }
}
