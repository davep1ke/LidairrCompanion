using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using LidarrCompanion.Helpers;
using LidarrCompanion.Models;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;

namespace LidarrCompanion.Services
{
    public class ImportResult
    {
        public int SuccessCount { get; set; }
        public int FailCount { get; set; }
    }

    public class ImportService
    {
        // Resolve server-local mapping shared helper. The pathKey indicates which configured path
        // we are matching against (only LidarrImportPath requires mapping). If serverToLocal is true,
        // rewrite server import paths to local mapping; if false, rewrite local mapping to server path.
        public static string ResolveMappedPath(string filePath, SettingKey pathKey, bool serverToLocal)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return string.Empty;

            // Only LidarrImportPath requires server/local mapping. Other keys are treated as literal paths.
            if (pathKey != SettingKey.LidarrImportPath)
            {
                throw new NotImplementedException();
            }

            var serverImportPath = AppSettings.GetValue(SettingKey.LidarrImportPath);
            var localMapping = AppSettings.GetValue(SettingKey.LidarrImportPathLocal);

            if (string.IsNullOrWhiteSpace(serverImportPath) || string.IsNullOrWhiteSpace(localMapping))
                return filePath;

            try
            {
                var normServer = serverImportPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var normLocal = localMapping.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                if (serverToLocal)
                {
                    if (filePath.StartsWith(normServer, StringComparison.OrdinalIgnoreCase))
                    {
                        var relative = filePath.Substring(normServer.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        return Path.Combine(normLocal, relative);
                    }
                }
                else
                {
                    if (filePath.StartsWith(normLocal, StringComparison.OrdinalIgnoreCase))
                    {
                        var relative = filePath.Substring(normLocal.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        return Path.Combine(normServer, relative);
                    }
                }
            }
            catch
            {
                // ignore and fall back to returning original path
            }

            return filePath;
        }

        public async Task<ImportResult> ImportAsync(List<ProposedAction> actionsSnapshot,
            ObservableCollection<LidarrManualImportFile> manualImportFiles,
            ObservableCollection<ProposedAction> proposedActions,
            ObservableCollection<LidarrArtistReleaseTrack> artistReleaseTracks,
            HashSet<int> assignedFileIds,
            HashSet<int> assignedTrackIds)
        {
            var result = new ImportResult();
            if (actionsSnapshot == null || actionsSnapshot.Count == 0)
                return result;

            var lidarr = new LidarrHelper();
            var backupRoot = AppSettings.GetValue(SettingKey.BackupRootFolder);
            var notSelectedRoot = AppSettings.GetValue(SettingKey.NotSelectedPath);
            var deferRoot = AppSettings.GetValue(SettingKey.DeferDestinationPath);

            // Determine whether general backup/copy features are enabled
            var backupBeforeImport = AppSettings.Current.GetTyped<bool>(SettingKey.BackupFilesBeforeImport);

            // Initialize statuses
            foreach (var a in actionsSnapshot)
            {
                a.ImportStatus = string.Empty;
                a.ErrorMessage = string.Empty;
            }

            // BACKUP - only if backup root configured AND overall backup enabled
            if (!string.IsNullOrWhiteSpace(backupRoot) && backupBeforeImport)
            {
                try
                {
                    BackupProposedActionFiles(actionsSnapshot);
                }
                catch (Exception ex)
                {
                    // set error on all actions and abort
                    foreach (var a in actionsSnapshot)
                    {
                        a.ImportStatus = "Failed";
                        a.ErrorMessage = "Backup failed: " + ex.Message;
                    }
                    MessageBox.Show($"Backup failed: {ex.Message}", "Backup Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return result;
                }
            }

            // MOVE Not Selected BEFORE import
            if (!string.IsNullOrWhiteSpace(notSelectedRoot))
            {
                var moveActions = actionsSnapshot.Where(a => a.IsMoveToNotSelected && !string.Equals(a.ActionType, "Defer", StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var ma in moveActions)
                {
                    try
                    {
                        ProcessMoveAction(ma, manualImportFiles, proposedActions, notSelectedRoot, SettingKey.CopyNotSelectedFiles);
                        ma.ImportStatus = "Success";
                    }
                    catch (Exception ex)
                    {
                        ma.ImportStatus = "Failed";
                        ma.ErrorMessage = ex.Message;
                        // Abort all further processing - imports must not run
                        MessageBox.Show($"Move to NotSelected failed for '{ma.OriginalFileName}': {ex.Message}", "Move Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        return result;
                    }
                }

                // Remove moved actions from snapshot so they aren't processed further
                actionsSnapshot = actionsSnapshot.Where(a => !(a.IsMoveToNotSelected && !string.Equals(a.ActionType, "Defer", StringComparison.OrdinalIgnoreCase))).ToList();
            }

            // MOVE Deferred BEFORE import (after backup step)
            if (!string.IsNullOrWhiteSpace(deferRoot))
            {
                var deferActions = actionsSnapshot.Where(a => string.Equals(a.ActionType, "Defer", StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var da in deferActions)
                {
                    try
                    {
                        ProcessMoveAction(da, manualImportFiles, proposedActions, deferRoot, SettingKey.CopyDeferredFiles);
                        da.ImportStatus = "Success";
                    }
                    catch (Exception ex)
                    {
                        da.ImportStatus = "Failed";
                        da.ErrorMessage = ex.Message;
                        MessageBox.Show($"Defer move failed for '{da.OriginalFileName}': {ex.Message}", "Move Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        return result;
                    }
                }

                actionsSnapshot = actionsSnapshot.Where(a => !string.Equals(a.ActionType, "Defer", StringComparison.OrdinalIgnoreCase)).ToList();
            }

            // IMPORT remaining actions grouped by OriginalRelease
            var importGroups = actionsSnapshot.Where(a => !a.IsNotForImport).GroupBy(a => a.OriginalRelease);
            foreach (var group in importGroups)
            {
                var actionsForRelease = group.ToList();
                try
                {
                    var success = await lidarr.ImportFilesAsync(actionsForRelease);
                    if (success)
                    {
                        foreach (var a in actionsForRelease)
                        {
                            // Mark success by default; if later copying fails we will mark error
                            a.ImportStatus = "Success";
                        }

                        result.SuccessCount += actionsForRelease.Count;

                        foreach (var a in actionsForRelease)
                        {
                            var fileRow = manualImportFiles.FirstOrDefault(f => f.Id == a.FileId);

                            // After successful import, optionally copy imported file to secondary copy location (non-fatal)
                            try
                            {
                                var copyImported = AppSettings.Current.GetTyped<bool>(SettingKey.CopyImportedFiles);
                                var copyDestRoot = AppSettings.GetValue(SettingKey.CopyImportedFilesPath);
                                if (copyImported && !string.IsNullOrWhiteSpace(copyDestRoot))
                                {
                                    // Determine the import destination folder where Lidarr placed the imported file.
                                    var importDestFolder = ResolveMappedPath(a.Path, SettingKey.LidarrImportPath, true);
                                    string sourceToCopy = string.Empty;

                                    if (!string.IsNullOrWhiteSpace(importDestFolder) && Directory.Exists(importDestFolder))
                                    {
                                        try
                                        {
                                            // Prefer files with same extension as original file when possible
                                            var origExt = fileRow != null ? Path.GetExtension(fileRow.Path) : Path.GetExtension(a.Path);
                                            var candidates = Directory.GetFiles(importDestFolder);
                                            if (!string.IsNullOrWhiteSpace(origExt))
                                            {
                                                var withExt = candidates.Where(c => string.Equals(Path.GetExtension(c), origExt, StringComparison.OrdinalIgnoreCase)).ToArray();
                                                if (withExt.Length > 0) candidates = withExt;
                                            }

                                            if (candidates.Length > 0)
                                            {
                                                // pick newest file
                                                sourceToCopy = candidates.OrderByDescending(f => File.GetLastWriteTimeUtc(f)).First();
                                            }
                                        }
                                        catch
                                        {
                                            sourceToCopy = string.Empty;
                                        }
                                    }

                                    // If we didn't find an imported file in the import destination, warn and set error on action
                                    if (string.IsNullOrWhiteSpace(sourceToCopy) || !File.Exists(sourceToCopy))
                                    {
                                        a.ImportStatus = "Failed";
                                        a.ErrorMessage = "Imported file not found in import destination";
                                        result.FailCount++;
                                        MessageBox.Show($"Imported file not found in import destination for '{a.OriginalFileName}'. Skipping copy to secondary location.", "Copy Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                                    }
                                    else
                                    {
                                        var copyDest = Path.Combine(copyDestRoot, a.OriginalRelease ?? string.Empty);
                                        TryCopyFile(sourceToCopy, Path.Combine(copyDest, Path.GetFileName(sourceToCopy)), "copy imported file");
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                a.ImportStatus = "Failed";
                                a.ErrorMessage = ex.Message;
                                result.FailCount++;
                            }

                            if (fileRow != null) manualImportFiles.Remove(fileRow);
                            // Do not remove the proposed action here; caller will remove successful ones
                        }
                    }
                    else
                    {
                        // ImportFilesAsync reported failure for whole batch - mark each action failed
                        foreach (var a in actionsForRelease)
                        {
                            a.ImportStatus = "Failed";
                            a.ErrorMessage = "Import failed (Lidarr returned failure)";
                            result.FailCount++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Exception during import - mark all actions in group as failed with the exception
                    foreach (var a in actionsForRelease)
                    {
                        a.ImportStatus = "Failed";
                        a.ErrorMessage = ex.Message;
                        result.FailCount++;
                    }
                }
            }

            // Clear assigned trackers for successful imports/moves
            assignedFileIds.Clear();
            assignedTrackIds.Clear();
            foreach (var track in artistReleaseTracks)
                track.IsAssigned = false;

            return result;
        }

        /// <summary>
        /// Shared processing for moving a proposed action file to a destination root (not-selected or defer)
        /// Will perform move (copy+delete or move), optional secondary copy based on provided copyFlagKey,
        /// remove the proposal and the manual import file entry. Primary failures show a MessageBox and are rethrown.
        /// </summary>
        private void ProcessMoveAction(ProposedAction action,
            ObservableCollection<LidarrManualImportFile> manualImportFiles,
            ObservableCollection<ProposedAction> proposedActions,
            string rootDestination,
            SettingKey copyFlagKey)
        {
            // Try to find the manual import file row; if not present, fall back to using the ProposedAction.Path
            var fileRow = manualImportFiles.FirstOrDefault(f => f.Id == action.FileId);

            // Resolve source path: prefer fileRow.Path when available, otherwise use action.Path
            string sourcePath = string.Empty;
            if (fileRow != null)
            {
                sourcePath = ResolveMappedPath(fileRow.Path, SettingKey.LidarrImportPath, true);
            }
            else
            {
                sourcePath = ResolveMappedPath(action.Path, SettingKey.LidarrImportPath, true);
            }

            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                throw new IOException($"Source file for action not found: '{sourcePath}'");

            var sourceFolder = Path.GetDirectoryName(sourcePath) ?? string.Empty;

            var destDir = string.IsNullOrWhiteSpace(action.MoveDestinationPath) ? Path.Combine(rootDestination, action.OriginalRelease ?? string.Empty) : action.MoveDestinationPath;
            string destPath;
            try
            {
                destPath = MoveFileToDestination(sourcePath, destDir);

                // Ensure the destination exists after the move (MoveFileToDestination should throw on failure)
                if (string.IsNullOrWhiteSpace(destPath) || !File.Exists(destPath))
                {
                    // If destination doesn't exist, treat as failure
                    throw new IOException($"Move operation did not produce destination file for source '{sourcePath}' to '{destDir}'.");
                }
            }
            catch
            {
                // MoveFileToDestination already shows an error MessageBox. Rethrow to abort.
                throw;
            }

            // Optionally copy moved file to secondary copy location (non-fatal)
            try
            {
                var copyEnabled = AppSettings.Current.GetTyped<bool>(copyFlagKey);
                var copyDestRoot = AppSettings.GetValue(SettingKey.CopyImportedFilesPath);
                if (copyEnabled && !string.IsNullOrWhiteSpace(copyDestRoot))
                {
                    var copyDest = Path.Combine(copyDestRoot, action.OriginalRelease ?? string.Empty);
                    TryCopyFile(destPath, Path.Combine(copyDest, Path.GetFileName(destPath)), copyFlagKey == SettingKey.CopyDeferredFiles ? "copy deferred file" : "copy not-selected file");
                }
            }
            catch
            {
                // Ignore - TryCopyFile will have shown a MessageBox
            }

            // Remove any proposed actions related to this file (all of them)
            var related = proposedActions.Where(p => p.FileId == action.FileId).ToList();
            foreach (var r in related)
            {
                // Clear visual flag on manual import file if present
                var f = manualImportFiles.FirstOrDefault(x => x.Id == r.FileId);
                if (f != null) f.IsMarkedNotSelected = false;
                proposedActions.Remove(r);
            }

            // Remove the manual import file entry if it was present
            if (fileRow != null)
            {
                manualImportFiles.Remove(fileRow);
            }

            // If the source folder is now empty (all files moved), attempt to delete it. Non-fatal.
            try
            {
                if (!string.IsNullOrWhiteSpace(sourceFolder) && Directory.Exists(sourceFolder))
                {
                    // Only delete if directory is empty
                    if (!Directory.EnumerateFileSystemEntries(sourceFolder).Any())
                    {
                        Directory.Delete(sourceFolder);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to delete empty source folder '{sourceFolder}': {ex.Message}", "Folder Delete Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>
        /// Try to copy a file to a secondary location. Shows MessageBox on failure but does not throw.
        /// </summary>
        private void TryCopyFile(string sourceFile, string destFile, string operationDescription)
        {
            try
            {
                var destDir = Path.GetDirectoryName(destFile) ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(destDir)) Directory.CreateDirectory(destDir);
                File.Copy(sourceFile, destFile, true);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to {operationDescription} from '{sourceFile}' to '{destFile}': {ex.Message}", "Copy Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BackupProposedActionFiles(List<ProposedAction> actionsSnapshot)
        {
            // Read backup root from settings (caller ensures configured before calling)
            var backupRoot = AppSettings.GetValue(SettingKey.BackupRootFolder);
            if (string.IsNullOrWhiteSpace(backupRoot)) return;

            var groupsForBackup = actionsSnapshot.GroupBy(a => a.OriginalRelease);
            foreach (var group in groupsForBackup)
            {
                var releaseKey = group.Key ?? string.Empty;
                var filePaths = group.Select(a => a.Path).ToList();
                if (filePaths == null || filePaths.Count == 0)
                    throw new InvalidOperationException($"No file paths found for proposed actions in release '{releaseKey}'.");

                var samplePath = filePaths.FirstOrDefault() ?? string.Empty;
                var defaultFolderName = string.IsNullOrWhiteSpace(releaseKey) ? (Path.GetFileName(samplePath) ?? "release") : releaseKey;
                var destFolder = Path.Combine(backupRoot, defaultFolderName);
                Directory.CreateDirectory(destFolder);

                foreach (var filePath in filePaths)
                {
                    if (string.IsNullOrWhiteSpace(filePath))
                        throw new InvalidOperationException($"Proposed action contains an empty Path for release '{releaseKey}'.");

                    // Translate remote/server path to local mapped path if configured
                    var resolvedPath = ResolveMappedPath(filePath, SettingKey.LidarrImportPath, true);

                    // If ResolveMappedPath returned empty, fall back to original
                    if (string.IsNullOrWhiteSpace(resolvedPath)) resolvedPath = filePath;

                    if (Directory.Exists(resolvedPath) || resolvedPath.EndsWith(Path.DirectorySeparatorChar) || resolvedPath.EndsWith(Path.AltDirectorySeparatorChar))
                        throw new InvalidOperationException($"Proposed action Path '{resolvedPath}' for release '{releaseKey}' is a directory, expected a file path.");

                    if (!Path.HasExtension(resolvedPath))
                    {
                        if (!File.Exists(resolvedPath))
                            throw new InvalidOperationException($"Proposed action Path '{resolvedPath}' for release '{releaseKey}' does not point to an existing file.");
                    }

                    if (!File.Exists(resolvedPath))
                        throw new InvalidOperationException($"Source file not found: '{resolvedPath}' for release '{releaseKey}'.");

                    var destFile = Path.Combine(destFolder, Path.GetFileName(resolvedPath));

                    if (File.Exists(destFile))
                    {
                        var srcInfoCheck = new FileInfo(resolvedPath);
                        var destInfoCheck = new FileInfo(destFile);
                        if (srcInfoCheck.Length == destInfoCheck.Length)
                            continue; // already backed up
                    }

                    try
                    {
                        File.Copy(resolvedPath, destFile, true);

                        if (!File.Exists(destFile))
                            throw new IOException($"Backup failed: destination file not created: '{destFile}'.");

                        var srcInfo = new FileInfo(resolvedPath);
                        var destInfo = new FileInfo(destFile);
                        if (srcInfo.Length != destInfo.Length)
                            throw new IOException($"Backup verification failed for '{resolvedPath}'. Source size: {srcInfo.Length}, Destination size: {destInfo.Length}.");
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Backup failed for '{resolvedPath}' to '{destFile}': {ex.Message}", "Backup Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        throw;
                    }
                }
            }
        }

        /// <summary>
        /// Move a file to the destination folder. Attempts copy+delete first, falls back to File.Move.
        /// Shows MessageBox on failure and rethrows the underlying exception.
        /// Returns the final destination path on success.
        /// </summary>
        private string MoveFileToDestination(string sourcePath, string destDir)
        {
            Directory.CreateDirectory(destDir);
            var destPath = Path.Combine(destDir, Path.GetFileName(sourcePath));

            // If destination already exists, prefer to keep it if identical.
            if (File.Exists(destPath))
            {
                try
                {
                    var srcInfo = new FileInfo(sourcePath);
                    var destInfo = new FileInfo(destPath);

                    // If sizes match, assume file already present - delete source and return
                    if (srcInfo.Exists && destInfo.Exists && srcInfo.Length == destInfo.Length)
                    {
                        try { File.Delete(sourcePath); } catch { /* ignore delete failure */ }
                        return destPath;
                    }

                    // Sizes differ - try to overwrite by copying and deleting the source
                    File.Copy(sourcePath, destPath, true);
                    File.Delete(sourcePath);

                    // Verify copy
                    destInfo = new FileInfo(destPath);
                    if (srcInfo.Length != destInfo.Length)
                        throw new IOException($"Verification failed after overwrite for '{sourcePath}' to '{destPath}'.");

                    return destPath;
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to move/overwrite file from '{sourcePath}' to '{destPath}': {ex.Message}", "Move Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    throw;
                }
            }

            // Destination does not exist - try a direct move first
            try
            {
                File.Move(sourcePath, destPath);
                return destPath;
            }
            catch
            {
                // If move fails, fallback to copy+delete
                try
                {
                    File.Copy(sourcePath, destPath, true);
                    File.Delete(sourcePath);

                    var srcInfo = new FileInfo(sourcePath);
                    var destInfo = new FileInfo(destPath);
                    // If source no longer exists, we can't compare sizes; just return destPath
                    if (!srcInfo.Exists || srcInfo.Length == destInfo.Length)
                        return destPath;

                    throw new IOException($"Verification failed after copy for '{sourcePath}' to '{destPath}'.");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to move file from '{sourcePath}' to '{destPath}': {ex.Message}", "Move Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    throw;
                }
            }
        }
    }
}
