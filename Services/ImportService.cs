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
        private const int MaxTrackFetchAttempts = 20;
        private const int TrackFetchDelayMs = 3000;

        #region File Operations

        private bool CopyFileToSecondary(string sourceFilePath, ProposedAction action, SettingKey copyFlagKey)
        {
            try
            {
                var copyEnabled = AppSettings.Current.GetTyped<bool>(copyFlagKey);
                var copyDestRoot = AppSettings.GetValue(SettingKey.CopyImportedFilesPath);
                
                if (!copyEnabled || string.IsNullOrWhiteSpace(copyDestRoot))
                {
                    Logger.Log("Secondary copy not enabled or path not configured", LogSeverity.Verbose, new { CopyEnabled = copyEnabled, DestRoot = copyDestRoot });
                    return true;
                }

                if (!File.Exists(sourceFilePath))
                {
                    Logger.Log($"Source file not found for secondary copy: {sourceFilePath}", LogSeverity.Medium, new { Source = sourceFilePath }, filePath: sourceFilePath);
                    throw new FileNotFoundException();
                }

                var destFile = Path.Combine(copyDestRoot, Path.GetFileName(sourceFilePath));
                Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);

                File.Copy(sourceFilePath, destFile, true);

                var success = File.Exists(destFile);
                Logger.Log(success ? $"Secondary copy successful: {destFile}" : $"Secondary copy verification failed: {destFile}", 
                    success ? LogSeverity.Low : LogSeverity.High, 
                    new { Source = sourceFilePath, Destination = destFile },
                    filePath: sourceFilePath);
                
                return success;
            }
            catch (Exception ex)
            {
                Logger.Log($"Secondary copy failed: {ex.Message}", LogSeverity.High, new { Source = sourceFilePath, Error = ex.Message }, filePath: sourceFilePath);
                return false;
            }
        }

        private void ValidateAndBackupFile(string filePath, string releaseKey, string destFile)
        {
            var resolvedPath = FileOperationsHelper.ResolveMappedPath(filePath, SettingKey.LidarrImportPath, true);
            if (string.IsNullOrWhiteSpace(resolvedPath)) resolvedPath = filePath;

            if (!FileOperationsHelper.ValidateIsFile(resolvedPath, out string errorMessage))
            {
                Logger.Log($"File validation failed: {errorMessage}", LogSeverity.High, new { FilePath = resolvedPath, Release = releaseKey }, filePath: resolvedPath);
                throw new InvalidOperationException($"Proposed action for release '{releaseKey}': {errorMessage}");
            }

            if (File.Exists(destFile))
            {
                var srcInfo = new FileInfo(resolvedPath);
                var destInfo = new FileInfo(destFile);
                if (srcInfo.Length == destInfo.Length)
                {
                Logger.Log($"File already backed up (size match): {destFile}", LogSeverity.Verbose, new { Source = resolvedPath, Destination = destFile }, filePath: resolvedPath);
                    return;
                }
            }

            PerformBackup(resolvedPath, destFile);
        }

        private void PerformBackup(string resolvedPath, string destFile)
        {
            try
            {
                Logger.Log($"Backing up file: {resolvedPath} -> {destFile}", LogSeverity.Verbose, new { Source = resolvedPath, Destination = destFile }, filePath: resolvedPath);
                File.Copy(resolvedPath, destFile, true);

                if (!File.Exists(destFile))
                    throw new IOException($"Backup failed: destination file not created: '{destFile}'.");

                var srcInfo = new FileInfo(resolvedPath);
                var destInfo = new FileInfo(destFile);
                
                if (srcInfo.Length != destInfo.Length)
                    throw new IOException($"Backup verification failed for '{resolvedPath}'. Source size: {srcInfo.Length}, Destination size: {destInfo.Length}.");

                Logger.Log($"File backed up successfully: {destFile}", LogSeverity.Verbose, new { Source = resolvedPath, Destination = destFile }, filePath: resolvedPath);
            }
            catch (Exception ex)
            {
                Logger.Log($"Backup failed: {ex.Message}", LogSeverity.Critical, new { Source = resolvedPath, Destination = destFile, Error = ex.Message }, filePath: resolvedPath);
                MessageBox.Show($"Backup failed for '{resolvedPath}' to '{destFile}': {ex.Message}", "Backup Error", MessageBoxButton.OK, MessageBoxImage.Error);
                throw;
            }
        }

        /*private void TryDeleteEmptyFolder(string sourceFolder)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(sourceFolder) && Directory.Exists(sourceFolder) && !Directory.EnumerateFileSystemEntries(sourceFolder).Any())
                {
                    Directory.Delete(sourceFolder);
                    Logger.Log($"Deleted empty source folder: {sourceFolder}", LogSeverity.Low, new { Folder = sourceFolder });
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to delete empty source folder (non-fatal): {ex.Message}", LogSeverity.Low, new { Folder = sourceFolder, Error = ex.Message });
            }
        }*/

        #endregion

        #region Backup Operations

        private void BackupProposedActionFiles(List<ProposedAction> actionsSnapshot)
        {
            Logger.Log($"Starting backup of {actionsSnapshot.Count} proposed action files", LogSeverity.Medium, new { ActionCount = actionsSnapshot.Count });

            var backupRoot = AppSettings.GetValue(SettingKey.BackupRootFolder);
            if (string.IsNullOrWhiteSpace(backupRoot))
            {
                Logger.Log("Backup root folder not configured", LogSeverity.Low);
                return;
            }

            foreach (var group in actionsSnapshot.GroupBy(a => a.OriginalRelease))
            {
                BackupReleaseGroup(group, backupRoot);
            }

            Logger.Log($"Backup completed successfully for all files", LogSeverity.Medium);
        }

        private void BackupReleaseGroup(IGrouping<string?, ProposedAction> group, string backupRoot)
        {
            var releaseKey = group.Key ?? string.Empty;
            var filePaths = group.Select(a => a.Path).ToList();

            if (filePaths.Count == 0)
                throw new InvalidOperationException($"No file paths found for proposed actions in release '{releaseKey}'.");

            Logger.Log($"Backing up {filePaths.Count} files for release: {releaseKey}", LogSeverity.Low, new { Release = releaseKey, FileCount = filePaths.Count });

            var defaultFolderName = string.IsNullOrWhiteSpace(releaseKey) ? (Path.GetFileName(filePaths.FirstOrDefault()) ?? "release") : releaseKey;
            var destFolder = Path.Combine(backupRoot, defaultFolderName);
            Directory.CreateDirectory(destFolder);

            foreach (var filePath in filePaths)
            {
                if (string.IsNullOrWhiteSpace(filePath))
                    throw new InvalidOperationException($"Proposed action contains an empty Path for release '{releaseKey}'.");

                var destFile = Path.Combine(destFolder, Path.GetFileName(filePath));
                ValidateAndBackupFile(filePath, releaseKey, destFile);
            }
        }

        #endregion

        #region Main Import Flow

        public async Task<ImportResult> ImportAsync(List<ProposedAction> actionsSnapshot,
            ObservableCollection<LidarrManualImportFile> manualImportFiles,
            ObservableCollection<ProposedAction> proposedActions,
            ObservableCollection<LidarrArtistReleaseTrack> artistReleaseTracks,
            HashSet<int> assignedFileIds,
            HashSet<int> assignedTrackIds)
        {
            Logger.Log($"ImportAsync started with {actionsSnapshot?.Count ?? 0} actions", LogSeverity.Medium, new { ActionCount = actionsSnapshot?.Count ?? 0 });

            var result = new ImportResult();
            if (actionsSnapshot == null || actionsSnapshot.Count == 0)
            {
                Logger.Log("No actions to import", LogSeverity.Low);
                return result;
            }

            InitializeActionStatuses(actionsSnapshot);

            var lidarr = new LidarrHelper();
            var backupRoot = AppSettings.GetValue(SettingKey.BackupRootFolder);
            var importRootSetting = AppSettings.GetValue(SettingKey.LidarrImportPath);
            var backupBeforeImport = AppSettings.Current.GetTyped<bool>(SettingKey.BackupFilesBeforeImport);

            Logger.Log("Import configuration loaded", LogSeverity.Low, new { BackupRoot = backupRoot, BackupEnabled = backupBeforeImport });

            // Phase 1: Backup
            if (!string.IsNullOrWhiteSpace(backupRoot) && backupBeforeImport)
            {
                if (!TryBackupFiles(actionsSnapshot, result))
                    return result;
            }

            // Phase 2: Process actions
            actionsSnapshot = await ProcessAllActions(actionsSnapshot, manualImportFiles, proposedActions, artistReleaseTracks, result, importRootSetting, lidarr);

            // Cleanup
            ClearAssignmentTrackers(assignedFileIds, assignedTrackIds, artistReleaseTracks);

            Logger.Log($"ImportAsync completed - Success: {result.SuccessCount}, Failed: {result.FailCount}", LogSeverity.Medium, new { Success = result.SuccessCount, Failed = result.FailCount });
            return result;
        }

        private void InitializeActionStatuses(List<ProposedAction> actionsSnapshot)
        {
            foreach (var a in actionsSnapshot)
            {
                a.ImportStatus = string.Empty;
                a.ErrorMessage = string.Empty;
            }
        }

        private bool TryBackupFiles(List<ProposedAction> actionsSnapshot, ImportResult result)
        {
            try
            {
                BackupProposedActionFiles(actionsSnapshot);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"Backup phase failed: {ex.Message}", LogSeverity.Critical, ex);
                foreach (var a in actionsSnapshot)
                {
                    a.ImportStatus = "Failed";
                    a.ErrorMessage = "Backup failed: " + ex.Message;
                }
                MessageBox.Show($"Backup failed: {ex.Message}", "Backup Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private async Task<List<ProposedAction>> ProcessAllActions(
            List<ProposedAction> actionsSnapshot,
            ObservableCollection<LidarrManualImportFile> manualImportFiles,
            ObservableCollection<ProposedAction> proposedActions,
            ObservableCollection<LidarrArtistReleaseTrack> artistReleaseTracks,
            ImportResult result,
            string importRootSetting,
            LidarrHelper lidarr)
        {
            // Pre-process: Check cover art requirements
            if (!await ProcessCoverArtRequirements(actionsSnapshot, result))
            {
                Logger.Log("Cover art pre-processing aborted by user", LogSeverity.Medium);
                return new List<ProposedAction>();
            }

            // Unlink actions
            actionsSnapshot = ProcessUnlinkActions(actionsSnapshot, manualImportFiles, proposedActions, importRootSetting, result);
            if (actionsSnapshot == null) return new List<ProposedAction>();

            // Dynamic destination actions (includes legacy mapping)
            actionsSnapshot = ProcessDestinationActions(actionsSnapshot, manualImportFiles, proposedActions, result);
            if (actionsSnapshot == null) return new List<ProposedAction>();

            // Delete actions
            actionsSnapshot = ProcessDeleteActions(actionsSnapshot, manualImportFiles, proposedActions, result);
            if (actionsSnapshot == null) return new List<ProposedAction>();

            // Import actions
            await ProcessImportActions(lidarr, actionsSnapshot, manualImportFiles, proposedActions, artistReleaseTracks, result);

            return actionsSnapshot;
        }

        private void ClearAssignmentTrackers(HashSet<int> assignedFileIds, HashSet<int> assignedTrackIds, ObservableCollection<LidarrArtistReleaseTrack> artistReleaseTracks)
        {
            assignedFileIds.Clear();
            assignedTrackIds.Clear();
            foreach (var track in artistReleaseTracks)
                track.IsAssigned = false;
        }

        #endregion

        #region Unlink Processing

        private List<ProposedAction>? ProcessUnlinkActions(
            List<ProposedAction> actionsSnapshot,
            ObservableCollection<LidarrManualImportFile> manualImportFiles,
            ObservableCollection<ProposedAction> proposedActions,
            string importRootSetting,
            ImportResult result)
        {
            if (string.IsNullOrWhiteSpace(importRootSetting))
                return actionsSnapshot;

            var unlinkActions = actionsSnapshot.Where(a => a.Action == ProposalActionType.Unlink).ToList();
            if (unlinkActions.Count == 0)
                return actionsSnapshot;

            Logger.Log($"Processing {unlinkActions.Count} Unlink actions", LogSeverity.Medium, new { Count = unlinkActions.Count });

            foreach (var ua in unlinkActions)
            {
                try
                {
                    Logger.Log($"Processing Unlink action for: {ua.OriginalFileName}", LogSeverity.Low, new { FileName = ua.OriginalFileName, FileId = ua.FileId }, filePath: ua.Path);
                    ProcessMoveAction(ua, manualImportFiles, proposedActions, importRootSetting, null);
                    ua.ImportStatus = "Success";
                }
                catch (Exception ex)
                {
                    Logger.Log($"Unlink action failed: {ex.Message}", LogSeverity.High, new { FileName = ua.OriginalFileName, Error = ex.Message });
                    ua.ImportStatus = "Failed";
                    ua.ErrorMessage = ex.Message;
                    MessageBox.Show($"Unlink move failed for '{ua.OriginalFileName}': {ex.Message}", "Move Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return null;
                }
            }

            return actionsSnapshot.Where(a => a.Action != ProposalActionType.Unlink).ToList();
        }

        #endregion

        #region Destination Processing

        private List<ProposedAction>? ProcessDestinationActions(
            List<ProposedAction> actionsSnapshot,
            ObservableCollection<LidarrManualImportFile> manualImportFiles,
            ObservableCollection<ProposedAction> proposedActions,
            ImportResult result)
        {
            var destinations = AppSettings.Current.ImportDestinations ?? new List<ImportDestination>();

            // Map legacy actions to destinations
            MapLegacyActions(actionsSnapshot, destinations);

            // Process all MoveToDestination actions
            var moveActions = actionsSnapshot.Where(a => a.Action == ProposalActionType.MoveToDestination).ToList();
            foreach (var destGroup in moveActions.GroupBy(a => a.DestinationName))
            {
                if (!ProcessDestinationGroup(destGroup, destinations, manualImportFiles, proposedActions, result))
                    return null;
            }

            return actionsSnapshot.Where(a => a.Action != ProposalActionType.MoveToDestination &&
                                             a.Action != ProposalActionType.NotForImport &&
                                             a.Action != ProposalActionType.Defer).ToList();
        }

        private void MapLegacyActions(List<ProposedAction> actionsSnapshot, List<ImportDestination> destinations)
        {
            var legacyNotSelected = actionsSnapshot.Where(a => a.Action == ProposalActionType.NotForImport).ToList();
            var legacyDefer = actionsSnapshot.Where(a => a.Action == ProposalActionType.Defer).ToList();

            if (!legacyNotSelected.Any() && !legacyDefer.Any())
                return;

            Logger.Log($"Mapping legacy actions: NotForImport={legacyNotSelected.Count}, Defer={legacyDefer.Count}", LogSeverity.Medium);

            MapLegacyActionType(legacyNotSelected, destinations.FirstOrDefault(), "NotForImport");
            MapLegacyActionType(legacyDefer, destinations.Skip(1).FirstOrDefault(), "Defer");
        }

        private void MapLegacyActionType(List<ProposedAction> actions, ImportDestination? destination, string actionName)
        {
            if (!actions.Any()) return;

            if (destination != null)
            {
                Logger.Log($"Mapping {actions.Count} {actionName} actions to destination: {destination.Name}", LogSeverity.Low);
                foreach (var action in actions)
                {
                    action.Action = ProposalActionType.MoveToDestination;
                    action.DestinationName = destination.Name;
                }
            }
            else
            {
                Logger.Log($"No destination configured for {actionName} actions", LogSeverity.High);
                foreach (var action in actions)
                {
                    action.ImportStatus = "Failed";
                    action.ErrorMessage = $"No destination configured for {actionName}. Please add a destination in Settings.";
                }
            }
        }

        private bool ProcessDestinationGroup(
            IGrouping<string?, ProposedAction> destGroup,
            List<ImportDestination> destinations,
            ObservableCollection<LidarrManualImportFile> manualImportFiles,
            ObservableCollection<ProposedAction> proposedActions,
            ImportResult result)
        {
            var destName = destGroup.Key;
            var dest = destinations.FirstOrDefault(d => d.Name == destName);

            if (dest == null || string.IsNullOrWhiteSpace(dest.DestinationPath))
            {
                Logger.Log($"Destination not found or path not configured: {destName}", LogSeverity.High, new { DestinationName = destName });
                foreach (var action in destGroup)
                {
                    action.ImportStatus = "Failed";
                    action.ErrorMessage = $"Destination '{destName}' not configured";
                }
                return true;
            }

            Logger.Log($"Processing {destGroup.Count()} actions for destination: {destName}", LogSeverity.Medium, new { DestinationName = destName, Count = destGroup.Count() });

            foreach (var action in destGroup)
            {
                try
                {
                    ProcessMoveAction(action, manualImportFiles, proposedActions, dest.DestinationPath, dest);
                    action.ImportStatus = "Success";
                }
                catch (Exception ex)
                {
                    Logger.Log($"Move to destination failed: {ex.Message}", LogSeverity.High, new { FileName = action.OriginalFileName, Destination = destName, Error = ex.Message });
                    action.ImportStatus = "Failed";
                    action.ErrorMessage = ex.Message;
                    MessageBox.Show($"Move to '{destName}' failed for '{action.OriginalFileName}': {ex.Message}", "Move Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return false;
                }
            }

            return true;
        }

        #endregion

        #region Move Action Processing

        private void ProcessMoveAction(ProposedAction action,
            ObservableCollection<LidarrManualImportFile> manualImportFiles,
            ObservableCollection<ProposedAction> proposedActions,
            string rootDestination,
            ImportDestination? destination)
        {
            Logger.Log($"ProcessMoveAction started for {action.Action}", LogSeverity.Verbose, new { Action = action.Action.ToString(), FileId = action.FileId, FileName = action.OriginalFileName });

            var sourcePath = ResolveSourcePath(action, manualImportFiles);
            ValidateSourceFile(sourcePath);

            var sourceFolder = Path.GetDirectoryName(sourcePath) ?? string.Empty;
            var (doCopy, destDir) = GetMoveConfiguration(action, destination, rootDestination);

            // Optional secondary copy
            if (doCopy)
                TrySecondaryCopy(sourcePath, action);

            // Move file using helper
            var destPath = FileOperationsHelper.MoveFileToDestination(sourcePath, destDir);
            ValidateMoveResult(sourcePath, destDir, destPath);

            // Cleanup
            CleanupAfterMove(action, manualImportFiles, proposedActions, sourceFolder);

            Logger.Log($"ProcessMoveAction completed successfully", LogSeverity.Verbose, new { DestPath = destPath });
        }

        private string ResolveSourcePath(ProposedAction action, ObservableCollection<LidarrManualImportFile> manualImportFiles)
        {
            var fileRow = manualImportFiles.FirstOrDefault(f => f.Id == action.FileId);
            return fileRow != null
                ? FileOperationsHelper.ResolveMappedPath(fileRow.Path, SettingKey.LidarrImportPath, true)
                : FileOperationsHelper.ResolveMappedPath(action.Path, SettingKey.LidarrImportPath, true);
        }

        private void ValidateSourceFile(string sourcePath)
        {
            if (!FileOperationsHelper.ValidateFileExists(sourcePath))
            {
                Logger.Log($"Source file not found for move action: {sourcePath}", LogSeverity.High, new { SourcePath = sourcePath }, filePath: sourcePath);
                throw new IOException($"Source file for action not found: '{sourcePath}'");
            }
        }

        private (bool doCopy, string destDir) GetMoveConfiguration(ProposedAction action, ImportDestination? destination, string rootDestination)
        {
            bool doCopy = destination?.CopyFiles ?? false;
            bool appendReleaseFolder = action.Action != ProposalActionType.Unlink;

            var destDir = rootDestination ?? string.Empty;
            if (appendReleaseFolder)
                destDir = Path.Combine(destDir, action.OriginalRelease ?? string.Empty);

            Logger.Log($"Move configuration: Copy={doCopy}, DestDir={destDir}", LogSeverity.Verbose, new { DoCopy = doCopy, DestDir = destDir });
            return (doCopy, destDir);
        }

        private void TrySecondaryCopy(string sourcePath, ProposedAction action)
        {
            try
            {
                var copyDestRoot = AppSettings.GetValue(SettingKey.CopyImportedFilesPath);
                if (!string.IsNullOrWhiteSpace(copyDestRoot))
                    CopyFileToSecondary(sourcePath, action, SettingKey.CopyImportedFiles);
            }
            catch (Exception ex)
            {
                Logger.Log($"Secondary copy failed (non-fatal): {ex.Message}", LogSeverity.Medium, new { FileName = action.OriginalFileName, Error = ex.Message }, filePath: sourcePath);
                MessageBox.Show($"Copy of moved file to secondary location failed for '{action.OriginalFileName}'. Continuing with move operation.", "Copy Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ValidateMoveResult(string sourcePath, string destDir, string destPath)
        {
            if (!FileOperationsHelper.ValidateFileExists(destPath))
            {
                Logger.Log($"Move operation did not produce destination file", LogSeverity.Critical, new { Source = sourcePath, DestDir = destDir }, filePath: sourcePath);
                throw new IOException($"Move operation did not produce destination file for source '{sourcePath}' to '{destDir}'.");
            }
        }

        private void CleanupAfterMove(ProposedAction action, ObservableCollection<LidarrManualImportFile> manualImportFiles, ObservableCollection<ProposedAction> proposedActions, string sourceFolder)
        {
            var related = proposedActions.Where(p => p.FileId == action.FileId).ToList();
            Logger.Log($"Removing {related.Count} related proposed actions", LogSeverity.Verbose, new { FileId = action.FileId, Count = related.Count });

            foreach (var r in related)
            {
                var f = manualImportFiles.FirstOrDefault(x => x.Id == r.FileId);
                if (f != null) f.ProposedActionType = null;
                proposedActions.Remove(r);
            }

            var fileRow = manualImportFiles.FirstOrDefault(f => f.Id == action.FileId);
            if (fileRow != null)
            {
                manualImportFiles.Remove(fileRow);
                Logger.Log("Removed manual import file entry", LogSeverity.Verbose, new { FileId = action.FileId });
            }

            FileOperationsHelper.TryDeleteEmptyDirectory(sourceFolder);
        }

        #endregion

        #region Delete Processing

        private List<ProposedAction>? ProcessDeleteActions(List<ProposedAction> actionsSnapshot,
            ObservableCollection<LidarrManualImportFile> manualImportFiles,
            ObservableCollection<ProposedAction> proposedActions,
            ImportResult result)
        {
            var deleteActions = actionsSnapshot.Where(a => a.Action == ProposalActionType.Delete).ToList();
            if (deleteActions.Count == 0)
            {
                Logger.Log("No Delete actions to process", LogSeverity.Verbose);
                return actionsSnapshot;
            }

            Logger.Log($"Processing {deleteActions.Count} Delete actions", LogSeverity.Medium, new { Count = deleteActions.Count });

            foreach (var da in deleteActions)
            {
                if (!ProcessSingleDeleteAction(da, manualImportFiles, proposedActions, result))
                    return null;
            }

            Logger.Log($"Delete actions completed - {result.SuccessCount} files deleted", LogSeverity.Medium, new { DeletedCount = result.SuccessCount });
            return actionsSnapshot.Where(a => a.Action != ProposalActionType.Delete).ToList();
        }

        private bool ProcessSingleDeleteAction(ProposedAction da, ObservableCollection<LidarrManualImportFile> manualImportFiles, ObservableCollection<ProposedAction> proposedActions, ImportResult result)
        {
            try
            {
                Logger.Log($"Processing Delete action for: {da.OriginalFileName}", LogSeverity.Low, new { FileName = da.OriginalFileName, FileId = da.FileId }, filePath: da.Path);

                var sourcePath = ResolveSourcePath(da, manualImportFiles);
                ValidateSourceFile(sourcePath);

                var sourceFolder = Path.GetDirectoryName(sourcePath) ?? string.Empty;
                File.Delete(sourcePath);

                if (File.Exists(sourcePath))
                    throw new IOException($"Delete operation did not remove file: '{sourcePath}'");

                Logger.Log($"File deleted successfully: {sourcePath}", LogSeverity.Low, new { SourcePath = sourcePath }, filePath: sourcePath);

                CleanupAfterMove(da, manualImportFiles, proposedActions, sourceFolder);

                da.ImportStatus = "Success";
                result.SuccessCount++;
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"Delete action failed: {ex.Message}", LogSeverity.High, new { FileName = da.OriginalFileName, Error = ex.Message });
                da.ImportStatus = "Failed";
                da.ErrorMessage = ex.Message;
                MessageBox.Show($"Delete failed for '{da.OriginalFileName}': {ex.Message}", "Delete Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        #endregion

        #region Import Processing

        private async Task ProcessImportActions(LidarrHelper lidarr,
            List<ProposedAction> actionsSnapshot,
            ObservableCollection<LidarrManualImportFile> manualImportFiles,
            ObservableCollection<ProposedAction> proposedActions,
            ObservableCollection<LidarrArtistReleaseTrack> artistReleaseTracks,
            ImportResult result)
        {
            var importGroups = actionsSnapshot.Where(a => a.Action == ProposalActionType.Import).GroupBy(a => a.OriginalRelease);
            var groupCount = importGroups.Count();

            if (groupCount == 0)
            {
                Logger.Log("No Import actions to process", LogSeverity.Verbose);
                return;
            }

            Logger.Log($"Processing {groupCount} import groups", LogSeverity.Medium, new { GroupCount = groupCount });

            foreach (var group in importGroups)
            {
                await ProcessImportGroup(group, lidarr, manualImportFiles, proposedActions, artistReleaseTracks, result);
            }

            Logger.Log($"Import actions completed - Success: {result.SuccessCount}, Failed: {result.FailCount}", LogSeverity.Medium, new { Success = result.SuccessCount, Failed = result.FailCount });
        }

        private async Task ProcessImportGroup(IGrouping<string?, ProposedAction> group, LidarrHelper lidarr, ObservableCollection<LidarrManualImportFile> manualImportFiles, ObservableCollection<ProposedAction> proposedActions, ObservableCollection<LidarrArtistReleaseTrack> artistReleaseTracks, ImportResult result)
        {
            var actionsForRelease = group.ToList();
            Logger.Log($"Processing import group for release: {group.Key}", LogSeverity.Low, new { Release = group.Key, ActionCount = actionsForRelease.Count });

            try
            {
                var success = await lidarr.ImportFilesAsync(actionsForRelease);
                if (success)
                {
                    await ProcessSuccessfulImport(actionsForRelease, lidarr, manualImportFiles, result, group.Key);
                }
                else
                {
                    MarkImportGroupAsFailed(actionsForRelease, result, group.Key);
                }
            }
            catch (Exception ex)
            {
                MarkImportGroupAsException(actionsForRelease, result, group.Key, ex);
            }
        }

        private async Task ProcessSuccessfulImport(List<ProposedAction> actionsForRelease, LidarrHelper lidarr, ObservableCollection<LidarrManualImportFile> manualImportFiles, ImportResult result, string? releaseKey)
        {
            Logger.Log($"Lidarr import command succeeded for release: {releaseKey}", LogSeverity.Low, new { Release = releaseKey });

            foreach (var a in actionsForRelease)
            {
                await ProcessSingleImportVerification(a, lidarr, manualImportFiles, result);
            }
        }

        private async Task ProcessSingleImportVerification(ProposedAction a, LidarrHelper lidarr, ObservableCollection<LidarrManualImportFile> manualImportFiles, ImportResult result)
        {
            Logger.Log($"Verifying import for track: {a.MatchedTrack}", LogSeverity.Verbose, new { TrackId = a.TrackId, AlbumReleaseId = a.AlbumReleaseId });

            var tf = await TryRetrieveTrackFile(a, lidarr);

            if (tf == null)
            {
                HandleFailedTrackRetrieval(a, manualImportFiles, result);
                return;
            }

            MarkImportSuccess(a, result);
            await TrySecondaryCopyForImport(a, tf);
        }

        private async Task<LidarrTrackFile?> TryRetrieveTrackFile(ProposedAction a, LidarrHelper lidarr)
        {
            for (int attempt = 1; attempt <= MaxTrackFetchAttempts; attempt++)
            {
                Logger.Log($"Attempt {attempt}/{MaxTrackFetchAttempts} to retrieve track from Lidarr", LogSeverity.Verbose, new { TrackId = a.TrackId, Attempt = attempt });

                try
                {
                    var tracks = await lidarr.GetTracksByReleaseAsync(a.AlbumReleaseId).ConfigureAwait(false);
                    var matched = tracks?.FirstOrDefault(t => t.Id == a.TrackId);

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
                    Logger.Log($"Failed to get track (attempt {attempt}): {ex.Message}", LogSeverity.Low, new { Attempt = attempt, Error = ex.Message });
                }

                if (attempt < MaxTrackFetchAttempts)
                    await Task.Delay(TrackFetchDelayMs).ConfigureAwait(false);
            }

            return null;
        }

        private void HandleFailedTrackRetrieval(ProposedAction a, ObservableCollection<LidarrManualImportFile> manualImportFiles, ImportResult result)
        {
            Logger.Log($"Could not retrieve track file after {MaxTrackFetchAttempts} attempts", LogSeverity.High, new { TrackId = a.TrackId, FileName = a.OriginalFileName });
            a.ImportStatus = "Failed";
            a.ErrorMessage = "Could not retrieve tracks for release from Lidarr after multiple attempts";
            result.FailCount++;

            var fileRowFail = manualImportFiles.FirstOrDefault(f => f.Id == a.FileId);
            if (fileRowFail != null)
            {
                var dispatcher = Application.Current?.Dispatcher ?? System.Windows.Threading.Dispatcher.CurrentDispatcher;
                dispatcher.BeginInvoke(new Action(() =>
                {
                    try { if (manualImportFiles.Contains(fileRowFail)) manualImportFiles.Remove(fileRowFail); }
                    catch { }
                }));
            }
        }

        private void MarkImportSuccess(ProposedAction a, ImportResult result)
        {
            a.ImportStatus = "Success";
            result.SuccessCount++;
        }

        private async Task TrySecondaryCopyForImport(ProposedAction a, LidarrTrackFile tf)
        {
            var copyEnabled = AppSettings.Current.GetTyped<bool>(SettingKey.CopyImportedFiles);
            var copyDestRoot = AppSettings.GetValue(SettingKey.CopyImportedFilesPath);

            if (!copyEnabled || string.IsNullOrWhiteSpace(copyDestRoot) || string.IsNullOrWhiteSpace(tf.Path))
                return;

            try
            {
                var resolvedTfPath = FileOperationsHelper.ResolveMappedPath(tf.Path, SettingKey.LidarrLibraryPath, true);
                Logger.Log($"Copying imported file to secondary location", LogSeverity.Low, new { SourcePath = tf.Path }, filePath: resolvedTfPath);
                var copySuccess = CopyFileToSecondary(resolvedTfPath, a, SettingKey.CopyImportedFiles);

                if (!copySuccess)
                {
                    Logger.Log($"Secondary copy failed for imported file", LogSeverity.Medium, new { ResolvedPath = resolvedTfPath, FileName = a.OriginalFileName }, filePath: resolvedTfPath);
                    MessageBox.Show($"Copy of imported file at resolved path '{resolvedTfPath}' failed for '{a.OriginalFileName}'.", "Copy Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Exception during secondary copy: {ex.Message}", LogSeverity.Medium, new { FileName = a.OriginalFileName, Error = ex.Message });
                MessageBox.Show($"Copy of imported file to secondary location aborted for '{a.OriginalFileName}'. Continuing.", "Copy Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void MarkImportGroupAsFailed(List<ProposedAction> actionsForRelease, ImportResult result, string? releaseKey)
        {
            Logger.Log($"Lidarr import command reported failure for release: {releaseKey}", LogSeverity.High, new { Release = releaseKey });
            foreach (var a in actionsForRelease)
            {
                a.ImportStatus = "Failed";
                a.ErrorMessage = "Import failed (Lidarr returned failure)";
                result.FailCount++;
            }
        }

        private void MarkImportGroupAsException(List<ProposedAction> actionsForRelease, ImportResult result, string? releaseKey, Exception ex)
        {
            Logger.Log($"Exception during import processing: {ex.Message}", LogSeverity.High, new { Release = releaseKey, Error = ex.Message });
            foreach (var a in actionsForRelease)
            {
                a.ImportStatus = "Failed";
                a.ErrorMessage = ex.Message;
                result.FailCount++;
            }
        }

        #endregion

        #region Cover Art Processing

        /// <summary>
        /// Pre-process step to check if any actions require cover art verification
        /// </summary>
        private async Task<bool> ProcessCoverArtRequirements(List<ProposedAction> actionsSnapshot, ImportResult result)
        {
            Logger.Log("Checking cover art requirements", LogSeverity.Low);

            var destinations = AppSettings.Current.ImportDestinations ?? new List<ImportDestination>();
            
            // Find all actions that target destinations requiring cover art
            var actionsRequiringCoverArt = actionsSnapshot
                .Where(a => a.Action == ProposalActionType.MoveToDestination || 
                           a.Action == ProposalActionType.Import)
                .Where(a =>
                {
                    // For MoveToDestination, check the destination's RequireArtwork setting
                    if (a.Action == ProposalActionType.MoveToDestination && !string.IsNullOrWhiteSpace(a.DestinationName))
                    {
                        var dest = destinations.FirstOrDefault(d => d.Name == a.DestinationName);
                        return dest?.RequireArtwork ?? false;
                    }
                    
                    // For Import actions, check if any destination requires artwork (this is configurable)
                    // For now, we'll skip import actions unless they also have a destination
                    return false;
                })
                .ToList();

            if (!actionsRequiringCoverArt.Any())
            {
                Logger.Log("No actions require cover art verification", LogSeverity.Verbose);
                return true;
            }

            Logger.Log($"Found {actionsRequiringCoverArt.Count} action(s) requiring cover art verification", LogSeverity.Medium, new { Count = actionsRequiringCoverArt.Count });

            // Show the cover art window on the UI thread
            bool? dialogResult = null;
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var coverArtWindow = new CoverArtWindow
                {
                    Owner = Application.Current.MainWindow
                };

                coverArtWindow.LoadActions(actionsRequiringCoverArt, destinations);
                dialogResult = coverArtWindow.ShowDialog();
            });

            if (dialogResult != true)
            {
                Logger.Log("Cover art process was aborted by user", LogSeverity.Medium);
                return false;
            }

            Logger.Log("Cover art pre-processing completed successfully", LogSeverity.Low);
            return true;
        }

        #endregion
    }
}
