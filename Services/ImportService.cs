using LidairrCompanion.Helpers;
using LidairrCompanion.Models;
using System.Collections.ObjectModel;
using System.IO;

namespace LidairrCompanion.Services
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
                return filePath;
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

            // BACKUP
            if (!string.IsNullOrWhiteSpace(backupRoot))
            {
                BackupProposedActionFiles(actionsSnapshot);
            }

            // MOVE Not Selected BEFORE import
            if (!string.IsNullOrWhiteSpace(notSelectedRoot))
            {
                var moveActions = actionsSnapshot.Where(a => a.IsMoveToNotSelected).ToList();
                foreach (var ma in moveActions)
                {
                    var fileRow = manualImportFiles.FirstOrDefault(f => f.Id == ma.FileId);
                    if (fileRow == null) continue;

                    // Resolve server path to local path for the source file
                    var sourcePath = ResolveMappedPath(fileRow.Path, SettingKey.LidarrImportPath, true);
                    if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                        throw new IOException($"Source file for move-to-not-selected not found: '{sourcePath}'");

                    var destDir = string.IsNullOrWhiteSpace(ma.MoveDestinationPath) ? Path.Combine(notSelectedRoot, ma.OriginalRelease ?? string.Empty) : ma.MoveDestinationPath;
                    Directory.CreateDirectory(destDir);
                    var destPath = Path.Combine(destDir, Path.GetFileName(sourcePath));

                    // Try copy+delete, fallback to move; if either fails, throw to abort the whole import
                    try
                    {
                        File.Copy(sourcePath, destPath, true);
                        File.Delete(sourcePath);
                    }
                    catch
                    {
                        // Let File.Move throw if it fails - abort the import
                        File.Move(sourcePath, destPath);
                    }

                    var toRemove = proposedActions.FirstOrDefault(p => p.FileId == ma.FileId && p.IsMoveToNotSelected);
                    if (toRemove != null)
                    {
                        var f = manualImportFiles.FirstOrDefault(x => x.Id == toRemove.FileId);
                        if (f != null) f.IsMarkedNotSelected = false;
                        proposedActions.Remove(toRemove);
                    }

                    manualImportFiles.Remove(fileRow);
                }

                actionsSnapshot = actionsSnapshot.Where(a => !a.IsMoveToNotSelected).ToList();
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
                        result.SuccessCount += actionsForRelease.Count;
                        foreach (var a in actionsForRelease)
                        {
                            var fileRow = manualImportFiles.FirstOrDefault(f => f.Id == a.FileId);
                            if (fileRow != null) manualImportFiles.Remove(fileRow);
                            proposedActions.Remove(a);
                        }
                    }
                    else
                    {
                        result.FailCount += actionsForRelease.Count;
                    }
                }
                catch
                {
                    result.FailCount += actionsForRelease.Count;
                }
            }

            // Clear assigned trackers for successful imports/moves
            assignedFileIds.Clear();
            assignedTrackIds.Clear();
            foreach (var track in artistReleaseTracks)
                track.IsAssigned = false;

            return result;
        }

        private void BackupProposedActionFiles(List<ProposedAction> actionsSnapshot)
        {
            // Read backup root from settings (caller ensures non-empty before calling)
            var backupRoot = AppSettings.GetValue(SettingKey.BackupRootFolder);
            if (string.IsNullOrWhiteSpace(backupRoot)) return;

            var serverImportPath = AppSettings.GetValue(SettingKey.LidarrImportPath);
            var localMapping = AppSettings.GetValue(SettingKey.LidarrImportPathLocal);

            var groupsForBackup = actionsSnapshot.GroupBy(a => a.OriginalRelease);
            foreach (var group in groupsForBackup)
            {
                var releaseKey = group.Key ?? string.Empty;
                var filePaths = group.Select(a => a.Path).ToList();
                if (filePaths == null || filePaths.Count ==0)
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

                    File.Copy(resolvedPath, destFile, true);

                    if (!File.Exists(destFile))
                        throw new IOException($"Backup failed: destination file not created: '{destFile}'.");

                    var srcInfo = new FileInfo(resolvedPath);
                    var destInfo = new FileInfo(destFile);
                    if (srcInfo.Length != destInfo.Length)
                        throw new IOException($"Backup verification failed for '{resolvedPath}'. Source size: {srcInfo.Length}, Destination size: {destInfo.Length}.");
                }
            }
        }
    }
}
