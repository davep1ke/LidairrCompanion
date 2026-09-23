using LidarrCompanion.Helpers;
using LidarrCompanion.Models;
using LidarrCompanion.Services;
using System.Collections.ObjectModel;

namespace LidarrCompanion.Web.Services
{
    // Separate tallies instead of one blended "success" count: a destination move can also trigger
    // a secondary copy, and blending those looked like 2 "successes" for one file. There's no
    // ImportSuccessCount here any more - a real Lidarr import is only confirmed once its
    // VerifyImport action settles, asynchronously (see VerifyImportService), not synchronously
    // within RunPipelineAsync - NewVerifyActions is how many were sent off to be confirmed.
    public class ImportResult
    {
        public int MoveSuccessCount { get; set; }
        public int SecondaryCopyCount { get; set; }
        public int FailCount { get; set; }

        // VerifyImport actions created while sending Import commands to Lidarr - the caller
        // (TriageService) enqueues them into VerifyImportService once RunPipelineAsync returns.
        public List<ProposedAction> NewVerifyActions { get; } = new();
    }

    // Ported from the WPF app's Services/ImportService.cs. The only real change is removing what
    // was WPF-coupled: MessageBox.Show calls (every failure path already Logger.Log's the same
    // information, and callers surface per-row ErrorMessage + a result summary to the UI instead
    // of a blocking dialog mid-pipeline) and Application.Current.Dispatcher (unnecessary - a
    // Blazor Server circuit already runs handlers on its own synchronization context).
    //
    // The original single ImportAsync method is split into PrepareImport (backup + the cover-art
    // pre-check, both fast/bounded) and RunPipelineAsync (unlink/destination/delete/import, the
    // actual work). The WPF version opened CoverArtWindow synchronously and blocked on it mid-
    // pipeline; that can't work server-side, and awaiting a human-timescale gate from inside a
    // single call would leave the triage page's busy state (pointer-events:none) on for as long
    // as the user takes to resolve it. Instead TriageService calls PrepareImport, and if any
    // items still need art, stashes them in CoverArtGateService and returns immediately - the
    // page stays usable, and RunPipelineAsync only runs once the /cover-art page resolves the
    // gate and calls back in.
    public class ImportRunner
    {
        // internal - VerifyImportService (which now owns the actual retry loop) shares these so
        // the "how many attempts, how far apart" policy lives in one place.
        internal const int MaxTrackFetchAttempts = 30;
        internal const int TrackFetchDelayMs = 5000;

        #region File Operations

        // Skipped (copying not enabled/configured) is deliberately distinct from Success - a
        // caller that only checked "truthy" would count a no-op copy as one it actually made.
        // internal (not private) - VerifyImportService reuses this for the post-verification
        // secondary copy, same as the synchronous Move/Unlink path does.
        internal enum CopyOutcome { Skipped, Success, Failed }

        internal CopyOutcome CopyFileToSecondary(string sourceFilePath, ProposedAction action, SettingKey copyFlagKey)
        {
            try
            {
                var copyEnabled = AppSettings.Current.GetTyped<bool>(copyFlagKey);
                var copyDestRoot = AppSettings.GetValue(SettingKey.CopyImportedFilesPath);

                if (!copyEnabled || string.IsNullOrWhiteSpace(copyDestRoot))
                {
                    Logger.Log("Secondary copy not enabled or path not configured", LogSeverity.Verbose, new { CopyEnabled = copyEnabled, DestRoot = copyDestRoot });
                    return CopyOutcome.Skipped;
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
                Logger.Log(success ? $"Secondary copy successful" : $"Secondary copy verification failed",
                    success ? LogSeverity.Low : LogSeverity.High,
                    new { Source = sourceFilePath, Destination = destFile },
                    filePath: destFile);

                return success ? CopyOutcome.Success : CopyOutcome.Failed;
            }
            catch (Exception ex)
            {
                Logger.Log($"Secondary copy failed: {ex.Message}", LogSeverity.High, new { Source = sourceFilePath, Error = ex.Message }, filePath: sourceFilePath);
                return CopyOutcome.Failed;
            }
        }

        private void ValidateAndBackupFile(string filePath, string releaseKey, string destFile)
        {
            var resolvedPath = FileOperationsHelper.ResolveMappedPathAnyKnown(filePath, true);
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
                throw;
            }
        }

        #endregion

        #region Backup Operations

        private void BackupProposedActionFiles(List<ProposedAction> actionsSnapshot)
        {
            // Actions that don't touch a source file (VerifyImport chief among them - by the time
            // one exists, its file has already been handed to Lidarr and may well be gone) or that
            // already have a confirmed backup from an earlier run never reach this loop, so a
            // reprocess can't fail validating a source file it was never going to back up again.
            var toBackUp = actionsSnapshot.Where(a => ImportActionRules.RequiresSourceFile(a.Action) && !a.BackedUp).ToList();

            Logger.Log($"Starting backup of {toBackUp.Count} proposed action files ({actionsSnapshot.Count} total actions)", LogSeverity.Medium, new { ActionCount = toBackUp.Count });

            var backupRoot = AppSettings.GetValue(SettingKey.BackupRootFolder);
            if (string.IsNullOrWhiteSpace(backupRoot))
            {
                Logger.Log("Backup root folder not configured", LogSeverity.Low);
                return;
            }

            foreach (var group in toBackUp.GroupBy(a => a.OriginalRelease))
            {
                BackupReleaseGroup(group, backupRoot);
            }

            Logger.Log($"Backup completed successfully for all files", LogSeverity.Medium);
        }

        private void BackupReleaseGroup(IGrouping<string?, ProposedAction> group, string backupRoot)
        {
            var releaseKey = group.Key ?? string.Empty;
            var actions = group.ToList();

            if (actions.Count == 0)
                throw new InvalidOperationException($"No file paths found for proposed actions in release '{releaseKey}'.");

            Logger.Log($"Backing up {actions.Count} files for release: {releaseKey}", LogSeverity.Low, new { Release = releaseKey, FileCount = actions.Count });

            var defaultFolderName = string.IsNullOrWhiteSpace(releaseKey) ? (Path.GetFileName(actions.FirstOrDefault()?.Path) ?? "release") : releaseKey;
            var destFolder = Path.Combine(backupRoot, defaultFolderName);
            Directory.CreateDirectory(destFolder);

            foreach (var action in actions)
            {
                if (string.IsNullOrWhiteSpace(action.Path))
                    throw new InvalidOperationException($"Proposed action contains an empty Path for release '{releaseKey}'.");

                var destFile = BackupPathHelper.ComputeBackupFilePath(backupRoot, defaultFolderName, action.Path);
                ValidateAndBackupFile(action.Path, releaseKey, destFile);
                action.BackedUp = true;
            }
        }

        #endregion

        #region Main Import Flow

        // Result of the fast, synchronous prepare step: backup (if enabled) plus the cover-art
        // pre-check. BackupFailed short-circuits the caller before anything else runs. Items in
        // ArtItemsToVerify cover every action whose destination requires artwork - both those
        // already satisfied (for visibility, matching the WPF app's combined list) and those
        // still missing it. The caller only needs to pause the pipeline when at least one is
        // missing (ArtItemsToVerify.Any(i => !i.HasCoverArt)); otherwise it can proceed straight
        // into RunPipelineAsync.
        public class PrepareResult
        {
            public bool BackupFailed { get; set; }
            public List<CoverArtQueueItem> ArtItemsToVerify { get; set; } = new();
        }

        public PrepareResult PrepareImport(List<ProposedAction> actionsSnapshot, ImportResult result)
        {
            InitializeActionStatuses(actionsSnapshot);

            var backupRoot = AppSettings.GetValue(SettingKey.BackupRootFolder);
            var backupBeforeImport = AppSettings.Current.GetTyped<bool>(SettingKey.BackupFilesBeforeImport);

            Logger.Log("Import configuration loaded", LogSeverity.Low, new { BackupRoot = backupRoot, BackupEnabled = backupBeforeImport });

            if (!string.IsNullOrWhiteSpace(backupRoot) && backupBeforeImport)
            {
                if (!TryBackupFiles(actionsSnapshot, result))
                    return new PrepareResult { BackupFailed = true };
            }

            var artItems = GetCoverArtCheckItems(actionsSnapshot);
            return new PrepareResult { ArtItemsToVerify = artItems };
        }

        public async Task<ImportResult> RunPipelineAsync(List<ProposedAction> actionsSnapshot,
            ObservableCollection<LidarrManualImportFile> manualImportFiles,
            ObservableCollection<ProposedAction> proposedActions,
            ObservableCollection<LidarrArtistReleaseTrack> artistReleaseTracks,
            HashSet<int> assignedFileIds,
            HashSet<int> assignedTrackIds)
        {
            Logger.Log($"RunPipelineAsync started with {actionsSnapshot?.Count ?? 0} actions", LogSeverity.Medium, new { ActionCount = actionsSnapshot?.Count ?? 0 });

            var result = new ImportResult();
            if (actionsSnapshot == null || actionsSnapshot.Count == 0)
            {
                Logger.Log("No actions to import", LogSeverity.Low);
                return result;
            }

            var lidarr = new LidarrHelper();
            var importRootSetting = AppSettings.GetValue(SettingKey.ImportPathLidarr);

            // Unlink actions
            actionsSnapshot = ProcessUnlinkActions(actionsSnapshot, manualImportFiles, proposedActions, importRootSetting, result) ?? new List<ProposedAction>();

            // Dynamic destination actions
            actionsSnapshot = ProcessDestinationActions(actionsSnapshot, manualImportFiles, proposedActions, result) ?? new List<ProposedAction>();

            // Delete actions
            actionsSnapshot = ProcessDeleteActions(actionsSnapshot, manualImportFiles, proposedActions, result) ?? new List<ProposedAction>();

            // Import actions
            var importGroups = actionsSnapshot.Where(a => a.Action == ProposalActionType.Import).GroupBy(a => a.OriginalRelease);
            Logger.Log($"Processing {importGroups.Count()} import groups", LogSeverity.Medium, new { GroupCount = importGroups.Count() });

            foreach (var group in importGroups)
            {
                await ProcessImportGroup(group, lidarr, manualImportFiles, proposedActions, artistReleaseTracks, result, actionsSnapshot);
            }

            // Cleanup
            ClearAssignmentTrackers(assignedFileIds, assignedTrackIds, artistReleaseTracks);

            Logger.Log($"RunPipelineAsync completed - Sent for verification: {result.NewVerifyActions.Count}, Moved: {result.MoveSuccessCount}, Copied: {result.SecondaryCopyCount}, Failed: {result.FailCount}",
                LogSeverity.Medium, new { SentForVerification = result.NewVerifyActions.Count, Moved = result.MoveSuccessCount, Copied = result.SecondaryCopyCount, Failed = result.FailCount });
            return result;
        }

        private void InitializeActionStatuses(List<ProposedAction> actionsSnapshot)
        {
            foreach (var a in actionsSnapshot)
            {
                a.Status = ImportActionStatus.Pending;
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
                Logger.Log($"Backup phase failed: {ex.Message}", LogSeverity.Critical, new { Error = ex.Message });
                // Only actions that genuinely needed (and didn't already have) a backup are put at
                // risk by this failure - one release's backup problem shouldn't fail an unrelated
                // action that was never going to touch its source file anyway.
                foreach (var a in actionsSnapshot.Where(a => ImportActionRules.RequiresSourceFile(a.Action) && !a.BackedUp))
                {
                    a.Status = ImportActionStatus.Failed;
                    a.ErrorMessage = "Backup failed: " + ex.Message;
                }
                return false;
            }
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
                    ProcessMoveAction(ua, manualImportFiles, proposedActions, importRootSetting, null, result);
                    ua.Status = ImportActionStatus.Success;
                }
                catch (Exception ex)
                {
                    Logger.Log($"Unlink action failed: {ex.Message}", LogSeverity.High, new { FileName = ua.OriginalFileName, Error = ex.Message });
                    ua.Status = ImportActionStatus.Failed;
                    ua.ErrorMessage = ex.Message;
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
                    action.Status = ImportActionStatus.Failed;
                    action.ErrorMessage = $"Destination '{destName}' not configured";
                }
                return true;
            }

            Logger.Log($"Processing {destGroup.Count()} actions for destination: {destName}", LogSeverity.Medium, new { DestinationName = destName, Count = destGroup.Count() });

            foreach (var action in destGroup)
            {
                try
                {
                    ProcessMoveAction(action, manualImportFiles, proposedActions, dest.DestinationPath, dest, result);
                    action.Status = ImportActionStatus.Success;
                    result.MoveSuccessCount++;
                }
                catch (Exception ex)
                {
                    Logger.Log($"Move to destination failed: {ex.Message}", LogSeverity.High, new { FileName = action.OriginalFileName, Destination = destName, Error = ex.Message });
                    action.Status = ImportActionStatus.Failed;
                    action.ErrorMessage = ex.Message;
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
            ImportDestination? destination,
            ImportResult result)
        {
            Logger.Log($"ProcessMoveAction started for {action.Action}", LogSeverity.Verbose, new { Action = action.Action.ToString(), FileId = action.FileId, FileName = action.OriginalFileName });

            var sourcePath = ResolveSourcePath(action, manualImportFiles);
            ValidateSourceFile(sourcePath);

            var sourceFolder = Path.GetDirectoryName(sourcePath) ?? string.Empty;
            var (doCopy, destDir) = GetMoveConfiguration(action, destination, rootDestination);

            if (doCopy)
                TrySecondaryCopy(sourcePath, action, result);

            var destPath = FileOperationsHelper.MoveFileToDestination(sourcePath, destDir);
            ValidateMoveResult(sourcePath, destDir, destPath);

            CleanupAfterMove(action, manualImportFiles, proposedActions, sourceFolder);

            Logger.Log($"ProcessMoveAction completed successfully", LogSeverity.Verbose, new { DestPath = destPath });
        }

        private string ResolveSourcePath(ProposedAction action, ObservableCollection<LidarrManualImportFile> manualImportFiles)
        {
            var fileRow = manualImportFiles.FirstOrDefault(f => f.Id == action.FileId);
            return fileRow != null
                ? FileOperationsHelper.ResolveMappedPathAnyKnown(fileRow.Path, true)
                : FileOperationsHelper.ResolveMappedPathAnyKnown(action.Path, true);
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

        private void TrySecondaryCopy(string sourcePath, ProposedAction action, ImportResult result)
        {
            try
            {
                var copyDestRoot = AppSettings.GetValue(SettingKey.CopyImportedFilesPath);
                if (string.IsNullOrWhiteSpace(copyDestRoot))
                    return;

                var outcome = CopyFileToSecondary(sourcePath, action, SettingKey.CopyImportedFiles);
                if (outcome == CopyOutcome.Success)
                    result.SecondaryCopyCount++;
            }
            catch (Exception ex)
            {
                Logger.Log($"Secondary copy failed (non-fatal): {ex.Message}", LogSeverity.Medium, new { FileName = action.OriginalFileName, Error = ex.Message }, filePath: sourcePath);
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

            var deletedCount = deleteActions.Count(a => a.Status == ImportActionStatus.Success);
            Logger.Log($"Delete actions completed - {deletedCount} files deleted", LogSeverity.Medium, new { DeletedCount = deletedCount });
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

                da.Status = ImportActionStatus.Success;
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"Delete action failed: {ex.Message}", LogSeverity.High, new { FileName = da.OriginalFileName, Error = ex.Message });
                da.Status = ImportActionStatus.Failed;
                da.ErrorMessage = ex.Message;
                return false;
            }
        }

        #endregion

        #region Import Processing

        private async Task ProcessImportGroup(IGrouping<string?, ProposedAction> group, LidarrHelper lidarr, ObservableCollection<LidarrManualImportFile> manualImportFiles, ObservableCollection<ProposedAction> proposedActions, ObservableCollection<LidarrArtistReleaseTrack> artistReleaseTracks, ImportResult result, List<ProposedAction> actionsSnapshot)
        {
            var actionsForRelease = group.ToList();
            var releaseKey = group.Key;
            Logger.Log($"Processing import group for release: {releaseKey}", LogSeverity.Low, new { Release = releaseKey, ActionCount = actionsForRelease.Count });

            try
            {
                var success = await lidarr.ImportFilesAsync(actionsForRelease);
                if (!success)
                {
                    MarkImportGroupFailed(actionsForRelease, result, releaseKey, "Import failed (Lidarr returned failure)");
                    return;
                }

                Logger.Log($"Lidarr import command succeeded for release: {releaseKey}", LogSeverity.Low, new { Release = releaseKey });

                foreach (var action in actionsForRelease)
                {
                    var verifyAction = new ProposedAction
                    {
                        Action = ProposalActionType.VerifyImport,
                        OriginalFileName = action.OriginalFileName,
                        OriginalRelease = action.OriginalRelease,
                        MatchedArtist = action.MatchedArtist,
                        MatchedTrack = action.MatchedTrack,
                        MatchedRelease = action.MatchedRelease,
                        TrackId = action.TrackId,
                        FileId = action.FileId,
                        ArtistId = action.ArtistId,
                        AlbumId = action.AlbumId,
                        AlbumReleaseId = action.AlbumReleaseId,
                        Path = action.Path,
                        DownloadId = action.DownloadId,
                        Quality = action.Quality,
                        RetryCount = 0,
                        MaxRetries = MaxTrackFetchAttempts,
                        Status = ImportActionStatus.Verifying
                    };

                    proposedActions.Add(verifyAction);
                    result.NewVerifyActions.Add(verifyAction);
                    Logger.Log($"Created VerifyImport action for track: {action.MatchedTrack}", LogSeverity.Verbose, new { TrackId = action.TrackId });

                    // Sent, not Success - Lidarr accepting the command isn't the same as the file
                    // actually landing, which the VerifyImport action above tracks going forward
                    // (asynchronously now - see VerifyImportService). TriageService treats Sent the
                    // same as Success for dropping this now-superseded row out of "Actions to Take".
                    action.Status = ImportActionStatus.Sent;
                }
            }
            catch (Exception ex)
            {
                MarkImportGroupFailed(actionsForRelease, result, releaseKey, ex.Message, ex);
            }
        }

        private void MarkImportGroupFailed(List<ProposedAction> actionsForRelease, ImportResult result, string? releaseKey, string errorMessage, Exception? ex = null)
        {
            var logMessage = ex != null ? $"Exception during import processing: {ex.Message}" : $"Lidarr import command reported failure for release: {releaseKey}";
            Logger.Log(logMessage, LogSeverity.High, ex != null ? new { Release = releaseKey, Error = ex.Message } : new { Release = releaseKey });

            foreach (var action in actionsForRelease)
            {
                action.Status = ImportActionStatus.Failed;
                action.ErrorMessage = errorMessage;
                result.FailCount++;
            }
        }

        #endregion

        #region Cover Art Processing

        // Ported from the WPF app's ImportService.ProcessCoverArtRequirements / CoverArtWindow.
        // LoadActions: find actions targeting a destination with RequireArtwork set, resolve each
        // file's real path, and check whether it already has embedded cover art. Returns every
        // matching item (both satisfied and missing) so the gate page can show the same combined
        // list the WPF window did; the caller decides whether anything actually needs pausing on.
        private List<CoverArtQueueItem> GetCoverArtCheckItems(List<ProposedAction> actionsSnapshot)
        {
            var destinations = AppSettings.Current.ImportDestinations ?? new List<ImportDestination>();

            var actionsRequiringCoverArt = actionsSnapshot
                .Where(a => a.Action == ProposalActionType.MoveToDestination && !string.IsNullOrWhiteSpace(a.DestinationName))
                .Where(a => destinations.FirstOrDefault(d => d.Name == a.DestinationName)?.RequireArtwork ?? false)
                .ToList();

            var items = new List<CoverArtQueueItem>();
            if (actionsRequiringCoverArt.Count == 0)
                return items;

            Logger.Log($"Found {actionsRequiringCoverArt.Count} action(s) requiring cover art verification", LogSeverity.Medium, new { Count = actionsRequiringCoverArt.Count });

            foreach (var action in actionsRequiringCoverArt)
            {
                var filePath = FileOperationsHelper.ResolveMappedPathAnyKnown(action.Path, true);
                if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                {
                    Logger.Log($"File not found for cover art check: {action.Path}", LogSeverity.Low);
                    continue;
                }

                var existingCoverArt = FileAndAudioService.ExtractCoverArt(filePath);
                var (artist, _, album, _) = FileAndAudioService.ExtractMetadata(filePath);

                items.Add(new CoverArtQueueItem
                {
                    Action = action,
                    FileName = Path.GetFileName(filePath),
                    FilePath = filePath,
                    ReleaseName = action.OriginalRelease ?? string.Empty,
                    DestinationName = action.DestinationName ?? "Import",
                    HasCoverArt = existingCoverArt is not null,
                    CoverArtPreview = existingCoverArt?.Data,
                    CoverArtMimeType = existingCoverArt?.MimeType,
                    Artist = artist,
                    Album = album
                });
            }

            return items;
        }

        #endregion
    }
}
