using LidarrCompanion.Helpers;
using LidarrCompanion.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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

            // Support mapping for import path and library root path
            string serverPath = string.Empty;
            string localMapping = string.Empty;

            if (pathKey == SettingKey.LidarrImportPath)
            {
                serverPath = AppSettings.GetValue(SettingKey.LidarrImportPath);
                localMapping = AppSettings.GetValue(SettingKey.LidarrImportPathLocal);
            }
            else if (pathKey == SettingKey.LidarrLibraryPath)
            {
                serverPath = AppSettings.GetValue(SettingKey.LidarrLibraryPath);
                localMapping = AppSettings.GetValue(SettingKey.LidarrLibraryPathLocal);
            }
            else
            {
                // For other keys, return original path unchanged
                return filePath;
            }

            if (string.IsNullOrWhiteSpace(serverPath) || string.IsNullOrWhiteSpace(localMapping))
                return filePath;

            try
            {
                var normServer = serverPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var normLocal = localMapping.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                if (serverToLocal)
                {
                    if (filePath.StartsWith(normServer, StringComparison.OrdinalIgnoreCase))
                    {
                        var relative = filePath.Substring(normServer.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '/','\\');
                        // Normalize separators to local OS style
                        if (!string.IsNullOrEmpty(relative))
                        {
                            relative = relative.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                        }
                        return string.IsNullOrEmpty(relative) ? normLocal : Path.Combine(normLocal, relative);
                    }
                }
                else
                {
                    if (filePath.StartsWith(normLocal, StringComparison.OrdinalIgnoreCase))
                    {
                        var relative = filePath.Substring(normLocal.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '/','\\');
                        // Determine server separator style from configured server path (prefer '/' if present)
                        var serverSep = normServer.Contains('/') ? '/' : '\\';
                        if (!string.IsNullOrEmpty(relative))
                        {
                            relative = relative.Replace('\\', serverSep).Replace('/', serverSep);
                        }
                        // Build server-style path using the chosen separator
                        if (string.IsNullOrEmpty(relative))
                            return normServer;
                        return normServer + serverSep + relative;
                    }
                }
            }
            catch
            {
                // ignore and fall back to returning original path
            }

            return filePath;
        }

        // Helper: centralise copying a found source file to the configured secondary copy root.
        // Returns true when copy was not required or succeeded; returns false when copy was required but source not found or copy failed.
        private bool CopyFileToSecondary(string sourceFilePath, ProposedAction action, SettingKey copyFlagKey)
        {
            try
            {
                var copyEnabled = AppSettings.Current.GetTyped<bool>(copyFlagKey);
                var copyDestRoot = AppSettings.GetValue(SettingKey.CopyImportedFilesPath);
                if (!copyEnabled || string.IsNullOrWhiteSpace(copyDestRoot))
                    return true; // nothing to do

             
                // If it's an existing file, use it
                if (!File.Exists(sourceFilePath))
                {
                    throw new FileNotFoundException(); 
                }


                var destFile = Path.Combine(copyDestRoot, Path.GetFileName(sourceFilePath));
                var destDir = Path.GetDirectoryName(destFile);
                
                Directory.CreateDirectory(destDir); 
                

                File.Copy(sourceFilePath, destFile, true);
                
                // Verify that the copy exists
                return File.Exists(destFile);
            }
            catch
            {
                // TryCopyFile will show any MessageBox; indicate failure
                return false;
            }
        }



        /// <summary>
        /// Move a file to the destination folder. Attempts copy+delete first, falls back to File.Move.
        /// Shows MessageBox on failure and rethrows the underlying exception.
        /// Returns the final destination path on success.
        /// </summary>
        private string MoveFileToDestination(string sourcePath, string destDir)
        {
            try
            {
                Directory.CreateDirectory(destDir);
            }
            catch
            {
                // Ignore directory creation failures here — proceed and let move/copy operations surface errors if directory truly unavailable.
            }
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



            // UNLINK - move files from subfolders into import root (no secondary copy)
            var importRootSetting = AppSettings.GetValue(SettingKey.LidarrImportPath);
            if (!string.IsNullOrWhiteSpace(importRootSetting))
            {
                var unlinkActions = actionsSnapshot.Where(a => a.Action == ProposalActionType.Unlink).ToList();
                foreach (var ua in unlinkActions)
                {
                    try
                    {
                        // ProcessMoveAction will resolve import root and never copy for Unlink actions
                        ProcessMoveAction(ua, manualImportFiles, proposedActions, importRootSetting);
                        ua.ImportStatus = "Success";
                    }
                    catch (Exception ex)
                    {
                        ua.ImportStatus = "Failed";
                        ua.ErrorMessage = ex.Message;
                        MessageBox.Show($"Unlink move failed for '{ua.OriginalFileName}': {ex.Message}", "Move Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        return result;
                    }
                }

                // Remove moved actions from snapshot so they aren't processed further
                actionsSnapshot = actionsSnapshot.Where(a => a.Action != ProposalActionType.Unlink).ToList();
            }

        // MOVE Not Selected
        if (!string.IsNullOrWhiteSpace(notSelectedRoot))
         {
             var moveActions = actionsSnapshot.Where(a => a.Action == ProposalActionType.NotForImport).ToList();
             foreach (var ma in moveActions)
             {
                 try
                 {
                     ProcessMoveAction(ma, manualImportFiles, proposedActions, notSelectedRoot);
                     ma.ImportStatus = "Success";
                 }
                 catch (Exception ex)
                 {
                     ma.ImportStatus = "Failed";
                     ma.ErrorMessage = ex.Message;
                     MessageBox.Show($"Move to NotSelected failed for '{ma.OriginalFileName}': {ex.Message}", "Move Error", MessageBoxButton.OK, MessageBoxImage.Error);
                     return result;
                 }
             }

             // Remove moved actions from snapshot so they aren't processed further
             actionsSnapshot = actionsSnapshot.Where(a => a.Action != ProposalActionType.NotForImport).ToList();
         }

         // MOVE Deferred
         if (!string.IsNullOrWhiteSpace(deferRoot))
         {
             var deferActions = actionsSnapshot.Where(a => a.Action == ProposalActionType.Defer).ToList();
             foreach (var da in deferActions)
             {
                 try
                 {
                     ProcessMoveAction(da, manualImportFiles, proposedActions, deferRoot);
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

             actionsSnapshot = actionsSnapshot.Where(a => a.Action != ProposalActionType.Defer).ToList();
         }

         // DELETE and IMPORT handled by dedicated helpers
         var postDeleteActions = ProcessDeleteActions(actionsSnapshot, manualImportFiles, proposedActions, result);
         if (postDeleteActions == null)
             return result; // abort on delete failure

         actionsSnapshot = postDeleteActions;

         await ProcessImportActions(lidarr, actionsSnapshot, manualImportFiles, proposedActions, artistReleaseTracks, result);


         // Clear assigned trackers for successful imports/moves
         assignedFileIds.Clear();
         assignedTrackIds.Clear();
         foreach (var track in artistReleaseTracks)
             track.IsAssigned = false;

         return result;
     }

     /// <summary>
     /// Shared processing for moving a proposed action file to a destination root.
     /// Behavior (secondary copy and destination computation) is determined from the ProposedAction.Action.
     /// - Import: checks CopyImportedFiles setting and appends release folder.
     /// - NotForImport: checks CopyNotSelectedFiles setting and appends release folder.
     /// - Defer: checks CopyDeferredFiles setting and appends release folder.
     /// - Unlink: never copies to secondary and always moves file into the configured LidarrImportPath (no release folder).
     /// </summary>
     private void ProcessMoveAction(ProposedAction action,
         ObservableCollection<LidarrManualImportFile> manualImportFiles,
         ObservableCollection<ProposedAction> proposedActions,
         string rootDestination)
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

         // Determine copy behavior and destination
         bool doCopy = false;
         SettingKey copyKey = SettingKey.CopyImportedFiles; // default
         bool appendReleaseFolder = true;
         string destRoot = rootDestination ?? string.Empty;

         switch (action.Action)
         {
             case ProposalActionType.Unlink:
                 // Never copy; move to configured import root folder (mapped)
                 doCopy = false;
                 appendReleaseFolder = false;
                 var importRoot = AppSettings.GetValue(SettingKey.LidarrImportPath);
                 destRoot = ResolveMappedPath(importRoot, SettingKey.LidarrImportPath, true);
                 break;
             case ProposalActionType.NotForImport:
                 copyKey = SettingKey.CopyNotSelectedFiles;
                 doCopy = AppSettings.Current.GetTyped<bool>(copyKey);
                 break;
             case ProposalActionType.Defer:
                 copyKey = SettingKey.CopyDeferredFiles;
                 doCopy = AppSettings.Current.GetTyped<bool>(copyKey);
                 break;
             case ProposalActionType.Import:
             default:
                 copyKey = SettingKey.CopyImportedFiles;
                 doCopy = AppSettings.Current.GetTyped<bool>(copyKey);
                 break;
         }

         // Optionally copy moved file to secondary location (non-fatal)
         if (doCopy)
         {
             try
             {
                 var copyDestRoot = AppSettings.GetValue(SettingKey.CopyImportedFilesPath);
                 if (!string.IsNullOrWhiteSpace(copyDestRoot))
                 {
                     CopyFileToSecondary(sourcePath, action, copyKey);
                 }
             }
             catch
             {
                 MessageBox.Show($"Copy of moved file to secondary location failed for '{action.OriginalFileName}'. Continuing with move operation.", "Copy Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
             }
         }

         // Compute destination directory at processing time
         var destDir = destRoot ?? string.Empty;
         if (appendReleaseFolder)
         {
             destDir = Path.Combine(destDir, action.OriginalRelease ?? string.Empty);
         }
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



         // Remove any proposed actions related to this file (all of them)
         var related = proposedActions.Where(p => p.FileId == action.FileId).ToList();
         foreach (var r in related)
         {
             // Clear visual flag on manual import file if present
             var f = manualImportFiles.FirstOrDefault(x => x.Id == r.FileId);
             if (f != null) f.ProposedActionType = null;
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

     // Helper: process delete actions synchronously. Returns filtered actionsSnapshot (without deletes) or null if aborted due to failure.
     private List<ProposedAction>? ProcessDeleteActions(List<ProposedAction> actionsSnapshot,
         ObservableCollection<LidarrManualImportFile> manualImportFiles,
         ObservableCollection<ProposedAction> proposedActions,
         ImportResult result)
     {
         var deleteActions = actionsSnapshot.Where(a => a.Action == ProposalActionType.Delete).ToList();
         if (deleteActions.Count == 0) return actionsSnapshot;

         foreach (var da in deleteActions)
         {
             try
             {
                 var fileRow = manualImportFiles.FirstOrDefault(f => f.Id == da.FileId);
                 var sourcePath = string.Empty;
                 if (fileRow != null)
                     sourcePath = ResolveMappedPath(fileRow.Path, SettingKey.LidarrImportPath, true);
                 else
                     sourcePath = ResolveMappedPath(da.Path, SettingKey.LidarrImportPath, true);

                 if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                     throw new IOException($"Source file for delete not found: '{sourcePath}'");

                 var sourceFolder = Path.GetDirectoryName(sourcePath) ?? string.Empty;
                 File.Delete(sourcePath);
                 if (File.Exists(sourcePath))
                     throw new IOException($"Delete operation did not remove file: '{sourcePath}'");

                 var related = proposedActions.Where(p => p.FileId == da.FileId).ToList();
                 foreach (var r in related)
                 {
                     var f = manualImportFiles.FirstOrDefault(x => x.Id == r.FileId);
                     if (f != null) f.ProposedActionType = null;
                     proposedActions.Remove(r);
                 }

                 if (fileRow != null) manualImportFiles.Remove(fileRow);

                 // Attempt to delete empty folder (non-fatal)
                 try
                 {
                     if (!string.IsNullOrWhiteSpace(sourceFolder) && Directory.Exists(sourceFolder))
                     {
                         if (!Directory.EnumerateFileSystemEntries(sourceFolder).Any())
                             Directory.Delete(sourceFolder);
                     }
                 }
                 catch { }

                 da.ImportStatus = "Success";
                 result.SuccessCount++;
             }
             catch (Exception ex)
             {
                 da.ImportStatus = "Failed";
                 da.ErrorMessage = ex.Message;
                 MessageBox.Show($"Delete failed for '{da.OriginalFileName}': {ex.Message}", "Delete Error", MessageBoxButton.OK, MessageBoxImage.Error);
                 return null; // abort
             }
         }

         return actionsSnapshot.Where(a => a.Action != ProposalActionType.Delete).ToList();
     }

     // Helper: process import actions asynchronously
     private async Task ProcessImportActions(LidarrHelper lidarr,
         List<ProposedAction> actionsSnapshot,
         ObservableCollection<LidarrManualImportFile> manualImportFiles,
         ObservableCollection<ProposedAction> proposedActions,
         ObservableCollection<LidarrArtistReleaseTrack> artistReleaseTracks,
         ImportResult result)
     {
         var importGroups = actionsSnapshot.Where(a => a.Action == ProposalActionType.Import).GroupBy(a => a.OriginalRelease);
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


                            bool importMarkedSuccess = false;

                            // Try to find the imported track from Lidarr's track list for this release
                            IList<LidarrTrack>? tracks = null;
                            const int maxAttempts = 20;
                            const int delayMs = 3000;
                            LidarrTrackFile? tf = null;

                            for (int attempt = 1; attempt <= maxAttempts; attempt++)
                            {
                                try
                                {
                                    tracks = await lidarr.GetTracksByReleaseAsync(a.AlbumReleaseId).ConfigureAwait(false);
                                }
                                catch
                                {
                                    // ignore and retry
                                    tracks = null;
                                }

                                var matched = tracks.FirstOrDefault(t => t.Id == a.TrackId);
                                if (matched != null)
                                {

                                    try
                                    {
                                        if (matched.TrackFileId > 0)
                                        {
                                            tf = await lidarr.GetTrackFileAsync(matched.TrackFileId).ConfigureAwait(false);
                                        }
                                    }
                                    catch
                                    {
                                        MessageBox.Show("Coulnd't get trackFile");
                                        // ignore failures fetching trackfile
                                    }

                                    // Only mark as success if we have a track file with a path
                                    if (tf != null && !string.IsNullOrWhiteSpace(tf.Path))
                                    {
                                        a.ImportStatus = "Success";
                                        result.SuccessCount++;
                                        importMarkedSuccess = true;
                                        break;
                                    }
                                    else
                                    {
                                        a.ImportStatus = "Failed";
                                        a.ErrorMessage = "Imported file record not found in Lidarr (no path returned)";
                                        result.FailCount++;
                                    }


                                    if (attempt < maxAttempts)
                                        await Task.Delay(delayMs).ConfigureAwait(false);
                                }

                            }

                            //if tf is null at this point, even waiting / retrying didnt help. Tidy up and move on
                            if (tf == null)
                            {
                                a.ImportStatus = "Failed";
                                a.ErrorMessage = "Could not retrieve tracks for release from Lidarr after multiple attempts";
                                result.FailCount++;
                                // remove manual file row if present and continue to next action
                                var fileRowFail = manualImportFiles.FirstOrDefault(f => f.Id == a.FileId);
                                if (fileRowFail != null)
                                {
                                    var dispatcherFail = Application.Current?.Dispatcher ?? System.Windows.Threading.Dispatcher.CurrentDispatcher;
                                    dispatcherFail.BeginInvoke(new Action(() =>
                                    {
                                        try { if (manualImportFiles.Contains(fileRowFail)) manualImportFiles.Remove(fileRowFail); } catch { }
                                    }));
                                }
                                continue;
                            }

                            // Show info regardless
                            //MessageBox.Show($"Imported Track: '{matched.Title}'\nTrackId: {matched.Id}\nTrackFileId: {matched.TrackFileId}\nPath: {tfPath}", "Imported Track", MessageBoxButton.OK, MessageBoxImage.Information);

                            // If we have a track file path, use it as the source for secondary copy
                            var copyEnabled = AppSettings.Current.GetTyped<bool>(SettingKey.CopyImportedFiles);
                            var copyDestRoot = AppSettings.GetValue(SettingKey.CopyImportedFilesPath);
                            if (copyEnabled && !string.IsNullOrWhiteSpace(copyDestRoot) && importMarkedSuccess && tf != null && !string.IsNullOrWhiteSpace(tf.Path))
                            {
                                try
                                {
                                    var resolvedTfPath = ResolveMappedPath(tf.Path, SettingKey.LidarrLibraryPath, true);
                                    var copySuccess = CopyFileToSecondary(resolvedTfPath, a, SettingKey.CopyImportedFiles);
                                    if (!copySuccess)
                                    {
                                        MessageBox.Show($"Copy of imported file at resolved path '{resolvedTfPath}' failed for  '{a.OriginalFileName}'.", "Copy Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                                    }

                                }
                                catch
                                {
                                    MessageBox.Show("Copy of imported file to secondary location aborted for '" + a.OriginalFileName + "'. Continuing.", "Copy Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                                }
                            }

                            else
                            {
                                // No matching track in Lidarr's release listing
                                a.ImportStatus = "Failed";
                                a.ErrorMessage = "Couldn't resolve link to imported track via Lidarr release listing";
                                result.FailCount++;
                            }
                            
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
     }
    }
}
