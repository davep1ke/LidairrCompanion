using LidarrCompanion.Helpers;
using LidarrCompanion.Models;
using LidarrCompanion.Services;
using System.Collections.ObjectModel;

namespace LidarrCompanion.Web.Services
{
    // Three separate tallies instead of one blended "success" count: a single logical import
    // (Mark Match -> Process Actions) can go through Lidarr's own import command AND a secondary
    // copy, or a destination move AND a secondary copy, and a blended count made those look like
    // 2-4 "successes" for what the user reasonably thinks of as one file.
    public class ImportResult
    {
        public int ImportSuccessCount { get; set; }
        public int MoveSuccessCount { get; set; }
        public int SecondaryCopyCount { get; set; }
        public int FailCount { get; set; }
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
        private const int MaxTrackFetchAttempts = 30;
        private const int TrackFetchDelayMs = 5000;

        #region File Operations

        // Skipped (copying not enabled/configured) is deliberately distinct from Success - a
        // caller that only checked "truthy" would count a no-op copy as one it actually made.
        private enum CopyOutcome { Skipped, Success, Failed }

        private CopyOutcome CopyFileToSecondary(string sourceFilePath, ProposedAction action, SettingKey copyFlagKey)
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

            // VerifyImport actions - process verification retries. This is where a real Lidarr
            // import actually gets confirmed (the command above can report success before Lidarr
            // has actually placed the file), so ImportSuccessCount is only incremented here, not
            // in ProcessImportGroup - counting both would double-count every real import.
            await ProcessVerifyImportActions(actionsSnapshot, lidarr, manualImportFiles, result);

            // Cleanup
            ClearAssignmentTrackers(assignedFileIds, assignedTrackIds, artistReleaseTracks);

            Logger.Log($"RunPipelineAsync completed - Imported: {result.ImportSuccessCount}, Moved: {result.MoveSuccessCount}, Copied: {result.SecondaryCopyCount}, Failed: {result.FailCount}",
                LogSeverity.Medium, new { Imported = result.ImportSuccessCount, Moved = result.MoveSuccessCount, Copied = result.SecondaryCopyCount, Failed = result.FailCount });
            return result;
        }

        private void InitializeActionStatuses(List<ProposedAction> actionsSnapshot)
        {
            foreach (var a in actionsSnapshot)
            {
                a.ImportStatus = string.Empty;
                a.ErrorMessage = string.Empty;

                if (a.Action == ProposalActionType.VerifyImport)
                {
                    a.RetryCount = 0;
                    a.LastRetryAttempt = null;
                    Logger.Log($"Reset retry tracking for VerifyImport action: {a.OriginalFileName}", LogSeverity.Verbose, new { FileName = a.OriginalFileName });
                }
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
                foreach (var a in actionsSnapshot)
                {
                    a.ImportStatus = "Failed";
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
                    ua.ImportStatus = "Success";
                }
                catch (Exception ex)
                {
                    Logger.Log($"Unlink action failed: {ex.Message}", LogSeverity.High, new { FileName = ua.OriginalFileName, Error = ex.Message });
                    ua.ImportStatus = "Failed";
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
                    ProcessMoveAction(action, manualImportFiles, proposedActions, dest.DestinationPath, dest, result);
                    action.ImportStatus = "Success";
                    result.MoveSuccessCount++;
                }
                catch (Exception ex)
                {
                    Logger.Log($"Move to destination failed: {ex.Message}", LogSeverity.High, new { FileName = action.OriginalFileName, Destination = destName, Error = ex.Message });
                    action.ImportStatus = "Failed";
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

            var deletedCount = deleteActions.Count(a => a.ImportStatus == "Success");
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

                da.ImportStatus = "Success";
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"Delete action failed: {ex.Message}", LogSeverity.High, new { FileName = da.OriginalFileName, Error = ex.Message });
                da.ImportStatus = "Failed";
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
                        ImportStatus = $"Post-Import Copy (0/{MaxTrackFetchAttempts})"
                    };

                    actionsSnapshot.Add(verifyAction);
                    proposedActions.Add(verifyAction);
                    Logger.Log($"Created VerifyImport action for track: {action.MatchedTrack}", LogSeverity.Verbose, new { TrackId = action.TrackId });

                    // Not counted as a success yet - Lidarr accepting the command isn't the same
                    // as the file actually landing, which VerifyImport below confirms. This just
                    // marks the original action done so it drops out of the "Actions to Take" list.
                    action.ImportStatus = "Success";
                }
            }
            catch (Exception ex)
            {
                MarkImportGroupFailed(actionsForRelease, result, releaseKey, ex.Message, ex);
            }
        }

        private async Task<LidarrTrackFile?> TryRetrieveTrackFile(ProposedAction action, LidarrHelper lidarr)
        {
            Logger.Log($"Attempting to retrieve track from Lidarr", LogSeverity.Verbose, new { TrackId = action.TrackId, Attempt = action.RetryCount + 1, Max = action.MaxRetries });

            try
            {
                var tracks = await lidarr.GetTracksByReleaseAsync(action.AlbumReleaseId).ConfigureAwait(false);
                var matched = tracks?.FirstOrDefault(t => t.Id == action.TrackId);

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
                Logger.Log($"Failed to get track: {ex.Message}", LogSeverity.Low, new { Attempt = action.RetryCount + 1, Error = ex.Message });
            }

            return null;
        }

        private async Task ProcessVerifyImportActions(List<ProposedAction> actionsSnapshot, LidarrHelper lidarr, ObservableCollection<LidarrManualImportFile> manualImportFiles, ImportResult result)
        {
            while (true)
            {
                var verifyActions = actionsSnapshot
                    .Where(a => a.Action == ProposalActionType.VerifyImport &&
                                a.ImportStatus != "Success" &&
                                a.ImportStatus != "Failed")
                    .ToList();

                if (verifyActions.Count == 0)
                {
                    Logger.Log("No VerifyImport actions to process", LogSeverity.Verbose);
                    return;
                }

                Logger.Log($"Processing {verifyActions.Count} VerifyImport actions", LogSeverity.Medium, new { Count = verifyActions.Count });

                var actionsToRequeue = new List<ProposedAction>();
                var actionsProcessed = false;

                foreach (var action in verifyActions)
                {
                    if (action.LastRetryAttempt.HasValue)
                    {
                        var timeSinceLastAttempt = DateTime.Now - action.LastRetryAttempt.Value;
                        if (timeSinceLastAttempt.TotalMilliseconds < TrackFetchDelayMs)
                        {
                            Logger.Log($"Skipping verification - minimum delay not met", LogSeverity.Verbose, new { FileName = action.OriginalFileName, TimeSinceLastMs = timeSinceLastAttempt.TotalMilliseconds, RequiredMs = TrackFetchDelayMs });
                            actionsToRequeue.Add(action);
                            continue;
                        }
                    }

                    action.LastRetryAttempt = DateTime.Now;
                    action.RetryCount++;
                    action.ImportStatus = $"Post-Import Copy ({action.RetryCount}/{action.MaxRetries})";

                    Logger.Log($"Processing VerifyImport for: {action.OriginalFileName}", LogSeverity.Low, new { FileName = action.OriginalFileName, Attempt = action.RetryCount, Max = action.MaxRetries });

                    var trackFile = await TryRetrieveTrackFile(action, lidarr);

                    if (trackFile != null)
                    {
                        action.ImportStatus = "Success";
                        result.ImportSuccessCount++;
                        await TrySecondaryCopyForImport(action, trackFile, result);

                        Logger.Log($"VerifyImport succeeded for: {action.OriginalFileName}", LogSeverity.Low, new { FileName = action.OriginalFileName });
                        actionsProcessed = true;
                    }
                    else if (action.RetryCount >= action.MaxRetries)
                    {
                        Logger.Log($"VerifyImport max retries reached: {action.OriginalFileName}", LogSeverity.High, new { FileName = action.OriginalFileName, Attempts = action.RetryCount });
                        action.ImportStatus = "Failed";
                        action.ErrorMessage = $"Could not verify import after {action.MaxRetries} attempts. Click Import again to retry.";
                        result.FailCount++;

                        var fileRowFail = manualImportFiles.FirstOrDefault(f => f.Id == action.FileId);
                        if (fileRowFail != null && manualImportFiles.Contains(fileRowFail))
                        {
                            manualImportFiles.Remove(fileRowFail);
                        }
                        actionsProcessed = true;
                    }
                    else
                    {
                        Logger.Log($"Requeueing VerifyImport action: {action.OriginalFileName}", LogSeverity.Verbose, new { FileName = action.OriginalFileName, Attempt = action.RetryCount });
                        actionsToRequeue.Add(action);
                    }
                }

                foreach (var action in actionsToRequeue)
                {
                    actionsSnapshot.Remove(action);
                    actionsSnapshot.Add(action);
                }

                if (actionsToRequeue.Count > 0)
                {
                    if (!actionsProcessed)
                    {
                        Logger.Log($"No actions ready to process. Waiting {TrackFetchDelayMs}ms before retry", LogSeverity.Low, new { RequeuedCount = actionsToRequeue.Count });
                        await Task.Delay(TrackFetchDelayMs);
                    }
                    else
                    {
                        Logger.Log($"Some actions processed. Continuing with {actionsToRequeue.Count} requeued actions", LogSeverity.Low, new { RequeuedCount = actionsToRequeue.Count });
                    }
                }
                else
                {
                    return;
                }
            }
        }

        private async Task TrySecondaryCopyForImport(ProposedAction a, LidarrTrackFile tf, ImportResult result)
        {
            var copyEnabled = AppSettings.Current.GetTyped<bool>(SettingKey.CopyImportedFiles);
            var copyDestRoot = AppSettings.GetValue(SettingKey.CopyImportedFilesPath);

            if (!copyEnabled || string.IsNullOrWhiteSpace(copyDestRoot) || string.IsNullOrWhiteSpace(tf.Path))
                return;

            try
            {
                var resolvedTfPath = FileOperationsHelper.ResolveMappedPath(tf.Path, SettingKey.LibraryPathLidarr, true);
                Logger.Log($"Copying imported file to secondary location", LogSeverity.Low, new { SourcePath = tf.Path }, filePath: resolvedTfPath);
                var outcome = CopyFileToSecondary(resolvedTfPath, a, SettingKey.CopyImportedFiles);

                if (outcome == CopyOutcome.Success)
                {
                    result.SecondaryCopyCount++;
                }
                else if (outcome == CopyOutcome.Failed)
                {
                    Logger.Log($"Secondary copy failed for imported file", LogSeverity.Medium, new { ResolvedPath = resolvedTfPath, FileName = a.OriginalFileName }, filePath: resolvedTfPath);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Exception during secondary copy: {ex.Message}", LogSeverity.Medium, new { FileName = a.OriginalFileName, Error = ex.Message });
            }

            await Task.CompletedTask;
        }

        private void MarkImportGroupFailed(List<ProposedAction> actionsForRelease, ImportResult result, string? releaseKey, string errorMessage, Exception? ex = null)
        {
            var logMessage = ex != null ? $"Exception during import processing: {ex.Message}" : $"Lidarr import command reported failure for release: {releaseKey}";
            Logger.Log(logMessage, LogSeverity.High, ex != null ? new { Release = releaseKey, Error = ex.Message } : new { Release = releaseKey });

            foreach (var action in actionsForRelease)
            {
                action.ImportStatus = "Failed";
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
