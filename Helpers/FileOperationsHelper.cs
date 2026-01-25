using LidarrCompanion.Models;
using System;
using System.IO;
using System.Linq;

namespace LidarrCompanion.Helpers
{
    /// <summary>
    /// Helper class for file operations including path mapping, moving, and copying files.
    /// </summary>
    public static class FileOperationsHelper
    {
        #region Path Mapping

        /// <summary>
        /// Resolves a file path by mapping between server and local paths based on configured settings.
        /// </summary>
        /// <param name="filePath">The file path to resolve</param>
        /// <param name="pathKey">The setting key indicating which path mapping to use</param>
        /// <param name="serverToLocal">If true, map from server to local; if false, map from local to server</param>
        /// <returns>The resolved file path, or the original path if mapping fails or is not configured</returns>
        public static string ResolveMappedPath(string filePath, SettingKey pathKey, bool serverToLocal)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return string.Empty;

            var (serverPath, localMapping) = AppSettings.GetPathMappingSettings(pathKey);
            if (string.IsNullOrWhiteSpace(serverPath) || string.IsNullOrWhiteSpace(localMapping))
                return filePath;

            try
            {
                var normServer = serverPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var normLocal = localMapping.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                return serverToLocal
                    ? MapServerToLocal(filePath, normServer, normLocal)
                    : MapLocalToServer(filePath, normLocal, normServer);
            }
            catch (Exception ex)
            {
                Logger.Log($"Path mapping failed: {ex.Message}", LogSeverity.Low, new { FilePath = filePath, PathKey = pathKey.ToString(), Error = ex.Message }, filePath: filePath);
                return filePath;
            }
        }

        /// <summary>
        /// Maps a server path to a local path.
        /// </summary>
        private static string MapServerToLocal(string filePath, string normServer, string normLocal)
        {
            if (!filePath.StartsWith(normServer, StringComparison.OrdinalIgnoreCase))
                return filePath;

            var relative = filePath.Substring(normServer.Length)
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '/', '\\');

            if (!string.IsNullOrEmpty(relative))
                relative = relative.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

            var result = string.IsNullOrEmpty(relative) ? normLocal : Path.Combine(normLocal, relative);
            Logger.Log($"Path mapping: {filePath} -> {result}", LogSeverity.Verbose, new { Original = filePath, Mapped = result, Direction = "ServerToLocal" }, filePath: result);
            return result;
        }

        /// <summary>
        /// Maps a local path to a server path.
        /// </summary>
        private static string MapLocalToServer(string filePath, string normLocal, string normServer)
        {
            if (!filePath.StartsWith(normLocal, StringComparison.OrdinalIgnoreCase))
                return filePath;

            var relative = filePath.Substring(normLocal.Length)
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '/', '\\');

            var serverSep = normServer.Contains('/') ? '/' : '\\';
            if (!string.IsNullOrEmpty(relative))
                relative = relative.Replace('\\', serverSep).Replace('/', serverSep);

            var result = string.IsNullOrEmpty(relative) ? normServer : normServer + serverSep + relative;
            Logger.Log($"Path mapping: {filePath} -> {result}", LogSeverity.Verbose, new { Original = filePath, Mapped = result, Direction = "LocalToServer" }, filePath: filePath);
            return result;
        }

        #endregion

        #region File Move Operations

        /// <summary>
        /// Moves a file to the destination directory, handling existing files and providing fallback strategies.
        /// </summary>
        /// <param name="sourcePath">The source file path</param>
        /// <param name="destDir">The destination directory</param>
        /// <returns>The final destination path of the moved file</returns>
        /// <exception cref="IOException">Thrown if the move operation fails</exception>
        public static string MoveFileToDestination(string sourcePath, string destDir)
        {
            Logger.Log($"Moving file: {sourcePath} to {destDir}", LogSeverity.Low, new { Source = sourcePath, DestDir = destDir }, filePath: sourcePath);

            try
            {
                Directory.CreateDirectory(destDir);
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to create destination directory: {ex.Message}", LogSeverity.Low, new { DestDir = destDir, Error = ex.Message }, filePath: destDir);
            }

            var destPath = Path.Combine(destDir, Path.GetFileName(sourcePath));

            if (File.Exists(destPath))
                return HandleExistingDestination(sourcePath, destPath);

            return TryMoveFile(sourcePath, destPath);
        }

        /// <summary>
        /// Handles the case where the destination file already exists.
        /// </summary>
        private static string HandleExistingDestination(string sourcePath, string destPath)
        {
            try
            {
                var srcInfo = new FileInfo(sourcePath);
                var destInfo = new FileInfo(destPath);

                if (srcInfo.Exists && destInfo.Exists && srcInfo.Length == destInfo.Length)
                {
                    Logger.Log($"Files identical (size match), removing source: {sourcePath}", LogSeverity.Low, new { Source = sourcePath, Size = srcInfo.Length }, filePath: sourcePath);
                    try { File.Delete(sourcePath); } catch { }
                    return destPath;
                }

                Logger.Log($"File sizes differ, overwriting destination", LogSeverity.Low, new { SrcSize = srcInfo.Length, DestSize = destInfo.Length }, filePath: destPath);
                File.Copy(sourcePath, destPath, true);
                File.Delete(sourcePath);

                destInfo = new FileInfo(destPath);
                if (srcInfo.Length != destInfo.Length)
                {
                Logger.Log($"Verification failed after overwrite", LogSeverity.High, new { Source = sourcePath, Dest = destPath, SrcSize = srcInfo.Length, DestSize = destInfo.Length }, filePath: destPath);
                    throw new IOException($"Verification failed after overwrite for '{sourcePath}' to '{destPath}'.");
                }

                Logger.Log($"File moved successfully (overwrite): {destPath}", LogSeverity.Low, new { Source = sourcePath, Destination = destPath }, filePath: destPath);
                return destPath;
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to move/overwrite file: {ex.Message}", LogSeverity.High, new { Source = sourcePath, Dest = destPath, Error = ex.Message }, filePath: sourcePath);
                throw new IOException($"Failed to move/overwrite file from '{sourcePath}' to '{destPath}': {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Attempts to move a file directly using File.Move.
        /// </summary>
        private static string TryMoveFile(string sourcePath, string destPath)
        {
            try
            {
                File.Move(sourcePath, destPath);
                Logger.Log($"File moved successfully (direct move): {destPath}", LogSeverity.Low, new { Source = sourcePath, Destination = destPath }, filePath: destPath);
                return destPath;
            }
            catch (Exception ex)
            {
                Logger.Log($"Direct move failed, attempting copy+delete: {ex.Message}", LogSeverity.Low, new { Source = sourcePath, Dest = destPath, Error = ex.Message }, filePath: sourcePath);
                return TryCopyAndDeleteFile(sourcePath, destPath);
            }
        }

        /// <summary>
        /// Fallback strategy: copy the file and then delete the source.
        /// </summary>
        private static string TryCopyAndDeleteFile(string sourcePath, string destPath)
        {
            try
            {
                File.Copy(sourcePath, destPath, true);
                File.Delete(sourcePath);

                var srcInfo = new FileInfo(sourcePath);
                var destInfo = new FileInfo(destPath);

                if (!srcInfo.Exists || srcInfo.Length == destInfo.Length)
                {
                    Logger.Log($"File moved successfully (copy+delete): {destPath}", LogSeverity.Low, new { Source = sourcePath, Destination = destPath }, filePath: destPath);
                    return destPath;
                }

                Logger.Log($"Verification failed after copy", LogSeverity.High, new { Source = sourcePath, Dest = destPath }, filePath: destPath);
                throw new IOException($"Verification failed after copy for '{sourcePath}' to '{destPath}'.");
            }
            catch (Exception ex)
            {
                Logger.Log($"Copy+delete fallback failed: {ex.Message}", LogSeverity.High, new { Source = sourcePath, Dest = destPath, Error = ex.Message }, filePath: sourcePath);
                throw new IOException($"Failed to move file from '{sourcePath}' to '{destPath}': {ex.Message}", ex);
            }
        }

        #endregion

        #region File Copy Operations

        /// <summary>
        /// Copies a file to a destination directory.
        /// </summary>
        /// <param name="sourceFilePath">The source file path</param>
        /// <param name="destDir">The destination directory</param>
        /// <param name="overwrite">Whether to overwrite if the file already exists</param>
        /// <returns>True if the copy was successful, false otherwise</returns>
        public static bool CopyFile(string sourceFilePath, string destDir, bool overwrite = true)
        {
            try
            {
                if (!File.Exists(sourceFilePath))
                {
                    Logger.Log($"Source file not found for copy: {sourceFilePath}", LogSeverity.Medium, new { Source = sourceFilePath }, filePath: sourceFilePath);
                    return false;
                }

                var destFile = Path.Combine(destDir, Path.GetFileName(sourceFilePath));
                Directory.CreateDirectory(destDir);

                File.Copy(sourceFilePath, destFile, overwrite);

                var success = File.Exists(destFile);
                Logger.Log(success ? $"File copied successfully: {destFile}" : $"Copy verification failed: {destFile}",
                    success ? LogSeverity.Low : LogSeverity.High,
                    new { Source = sourceFilePath, Destination = destFile },
                    filePath: destFile);

                return success;
            }
            catch (Exception ex)
            {
                Logger.Log($"File copy failed: {ex.Message}", LogSeverity.High, new { Source = sourceFilePath, Error = ex.Message }, filePath: sourceFilePath);
                return false;
            }
        }

        #endregion

        #region Directory Operations

        /// <summary>
        /// Attempts to delete a directory if it's empty.
        /// </summary>
        /// <param name="directoryPath">The directory path to delete</param>
        /// <returns>True if the directory was deleted or didn't exist, false otherwise</returns>
        public static bool TryDeleteEmptyDirectory(string directoryPath)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(directoryPath) && Directory.Exists(directoryPath) && !Directory.EnumerateFileSystemEntries(directoryPath).Any())
                {
                    Directory.Delete(directoryPath);
                    Logger.Log($"Deleted empty directory: {directoryPath}", LogSeverity.Low, new { Directory = directoryPath }, filePath: directoryPath);
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to delete empty directory (non-fatal): {ex.Message}", LogSeverity.Low, new { Directory = directoryPath, Error = ex.Message }, filePath: directoryPath);
                return false;
            }
        }

        /// <summary>
        /// Creates a directory if it doesn't exist.
        /// </summary>
        /// <param name="directoryPath">The directory path to create</param>
        /// <returns>True if the directory exists or was created successfully</returns>
        public static bool EnsureDirectoryExists(string directoryPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(directoryPath))
                    return false;

                if (!Directory.Exists(directoryPath))
                {
                    Directory.CreateDirectory(directoryPath);
                    Logger.Log($"Created directory: {directoryPath}", LogSeverity.Verbose, new { Directory = directoryPath }, filePath: directoryPath);
                }
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to create directory: {ex.Message}", LogSeverity.Medium, new { Directory = directoryPath, Error = ex.Message }, filePath: directoryPath);
                return false;
            }
        }

        #endregion

        #region File Validation

        /// <summary>
        /// Validates that a file exists and is accessible.
        /// </summary>
        /// <param name="filePath">The file path to validate</param>
        /// <returns>True if the file exists and is accessible</returns>
        public static bool ValidateFileExists(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return false;

            try
            {
                return File.Exists(filePath);
            }
            catch (Exception ex)
            {
                Logger.Log($"File validation failed: {ex.Message}", LogSeverity.Low, new { FilePath = filePath, Error = ex.Message }, filePath: filePath);
                return false;
            }
        }

        /// <summary>
        /// Validates that a path points to a file (not a directory) and exists.
        /// </summary>
        /// <param name="filePath">The path to validate</param>
        /// <param name="errorMessage">Output error message if validation fails</param>
        /// <returns>True if the path is a valid file</returns>
        public static bool ValidateIsFile(string filePath, out string errorMessage)
        {
            errorMessage = string.Empty;

            if (string.IsNullOrWhiteSpace(filePath))
            {
                errorMessage = "File path is empty or null.";
                return false;
            }

            if (Directory.Exists(filePath) || filePath.EndsWith(Path.DirectorySeparatorChar) || filePath.EndsWith(Path.AltDirectorySeparatorChar))
            {
                errorMessage = $"Path '{filePath}' is a directory, expected a file path.";
                return false;
            }

            if (!Path.HasExtension(filePath) && !File.Exists(filePath))
            {
                errorMessage = $"Path '{filePath}' does not point to an existing file.";
                return false;
            }

            if (!File.Exists(filePath))
            {
                errorMessage = $"File not found: '{filePath}'.";
                return false;
            }

            return true;
        }

        #endregion
    }
}
