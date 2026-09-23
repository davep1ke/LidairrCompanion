namespace LidarrCompanion.Helpers
{
    public static class BackupPathHelper
    {
        // Shared by ImportRunner.BackupReleaseGroup (writing a backup) and the "restore a failed
        // import from backup" feature (reading one back) - both need the exact same layout or a
        // restore will silently look in the wrong place. folderName is normally the release title;
        // callers fall back to the source file's own name when no release title is known.
        public static string ComputeBackupFilePath(string backupRoot, string folderName, string sourceFilePath) =>
            Path.Combine(backupRoot, folderName, Path.GetFileName(sourceFilePath));
    }
}
