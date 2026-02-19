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
        /// <summary>
        /// Actually perform the import of proposed actions into Lidarr.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private async void btn_Import_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;

            if (_proposedActions == null || !_proposedActions.Any())
            {
                Logger.Log("No proposed actions to import", LogSeverity.Low);
                MessageBox.Show("No proposed actions to import.", "Nothing to import", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            Logger.Log($"Starting import of {_proposedActions.Count} proposed actions", LogSeverity.Medium);
            SetBusy("Importing files to Lidarr...");
             try
             {
                 // Create snapshot - note that ImportService may add VerifyImport actions during processing
                 var actionsSnapshot = _proposedActions.ToList();

                 var result = await _importService.ImportAsync(actionsSnapshot, _manualImportFiles, _proposedActions, _artistReleaseTracks, _assignedFileIds, _assignedTrackIds);

                // After import, get the current state of _proposedActions which may include added VerifyImport actions
                // We need to check all actions that were in the original snapshot OR were added during processing
                var allProcessedActions = _proposedActions.Where(pa => 
                    !string.IsNullOrWhiteSpace(pa.ImportStatus) &&
                    (pa.ImportStatus.Equals("Success", StringComparison.OrdinalIgnoreCase) || 
                     pa.ImportStatus.Equals("Failed", StringComparison.OrdinalIgnoreCase))
                ).ToList();

                int removedCount = 0;
                int failedCount = 0;

                // Process all actions with a status
                foreach (var pa in allProcessedActions)
                {
                    Logger.Log($"Checking action: {pa.OriginalFileName}, Action={pa.Action}, Status={pa.ImportStatus}", LogSeverity.Verbose, new { FileName = pa.OriginalFileName, ActionType = pa.Action, Status = pa.ImportStatus });
                    
                    if (string.Equals(pa.ImportStatus, "Success", StringComparison.OrdinalIgnoreCase))
                    {
                        removedCount++; // count this as processed successfully
                        if (_proposedActions.Contains(pa))
                        {
                            Logger.Log($"Removing successful action from UI: {pa.OriginalFileName}", LogSeverity.Verbose, new { FileName = pa.OriginalFileName, ActionType = pa.Action });
                            _proposedActions.Remove(pa);
                        }
                        else
                        {
                            Logger.Log($"Action marked as Success but not found in _proposedActions: {pa.OriginalFileName}", LogSeverity.Medium, new { FileName = pa.OriginalFileName, ActionType = pa.Action });
                        }
                    }
                    else if (!string.IsNullOrWhiteSpace(pa.ImportStatus) && string.Equals(pa.ImportStatus, "Failed", StringComparison.OrdinalIgnoreCase))
                    {
                        // leave failed actions in the list and show error in MatchedRelease column
                        pa.MatchedRelease = pa.ErrorMessage;
                        failedCount++;
                        Logger.Log($"Import failed for file: {pa.OriginalFileName}", LogSeverity.High, new { FileId = pa.FileId, Error = pa.ErrorMessage });
                    }
                }

                Logger.Log($"Import completed - Success: {removedCount}, Failed: {failedCount}", 
                    failedCount > 0 ? LogSeverity.Medium : LogSeverity.Low,
                    new { Success = removedCount, Failed = failedCount });

                // Show summary
                if ((removedCount + failedCount) == 0)
                {
                    MessageBox.Show("No actions were processed.", "Import Results", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else if (failedCount > 0)
                {
                    MessageBox.Show($"Import completed. Success: {removedCount}, Failed: {failedCount}\n\nFailed actions remain in the list. Click Import again to retry them.", "Import Results", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                else
                {
                    MessageBox.Show($"Import completed. Success: {removedCount}", "Import Results", MessageBoxButton.OK, MessageBoxImage.Information);
                }

                // Ensure UI state is consistent (ImportService already clears assigned sets and track flags but refresh UI as well)
                _assignedFileIds.Clear();
                _assignedTrackIds.Clear();
            }
            catch (Exception ex)
            {
                Logger.Log($"Import exception: {ex.Message}", LogSeverity.Critical, ex);
                MessageBox.Show($"Import failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                ClearBusy();
            }
        }

        private void btn_ClearProposed_Click(object sender, RoutedEventArgs e)
        {
            // clear any file-level proposed action marks before clearing proposals
            foreach (var f in _manualImportFiles)
                f.ProposedActionType = null;

            _proposedActions.Clear();
        }

        private void btn_UnselectMatch_Click(object sender, RoutedEventArgs e)
        {
            if (list_Proposed_Actions.SelectedItem is not ProposedAction toRemove)
            {
                MessageBox.Show("Select a proposed action to unselect.", "No selection", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Find corresponding file and track items and clear assignments
            var file = _manualImportFiles.FirstOrDefault(f => f.Id == toRemove.FileId);
            var track = _artistReleaseTracks.FirstOrDefault(t => t.TrackId == toRemove.TrackId && t.Release == toRemove.MatchedRelease);

            if (file != null)
            {
                // clear file-level proposed action marker
                file.ProposedActionType = null;
                _assignedFileIds.Remove(file.Id);
            }

            if (track != null)
            {
                track.IsAssigned = false;
                _assignedTrackIds.Remove(track.TrackId);
            }

            _proposedActions.Remove(toRemove);

            // capture release context before removing
            var releaseKey = toRemove.OriginalRelease ?? string.Empty;

            // If there are no remaining assignment proposals for this release, remove any move-to-not-selected proposals for the same release
            var hasAssignmentsForRelease = _proposedActions.Any(p => p.Action == LidarrCompanion.Helpers.ProposalActionType.Import && (p.OriginalRelease ?? string.Empty) == releaseKey);
            if (!hasAssignmentsForRelease)
            {
                var movesToRemove = _proposedActions.Where(p => p.Action == LidarrCompanion.Helpers.ProposalActionType.NotForImport && (p.OriginalRelease ?? string.Empty) == releaseKey).ToList();
                foreach (var m in movesToRemove)
                {
                    // clear corresponding file highlight
                    var f = _manualImportFiles.FirstOrDefault(x => x.Id == m.FileId);
                    if (f != null) f.ProposedActionType = null;

                    _proposedActions.Remove(m);
                }
            }
        }
    }
}
