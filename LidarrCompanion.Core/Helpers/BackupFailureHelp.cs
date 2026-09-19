namespace LidarrCompanion.Helpers
{
    public static class BackupFailureHelp
    {
        // A "file not found" during the pre-import backup usually isn't a missing file: when the
        // music/backup shares are mounted on the Docker *host* after the container started (or were
        // remounted), the running container keeps seeing the empty folder it originally bound, and
        // no amount of retrying inside the app can fix that. Say so, since the raw error alone
        // sends people hunting for a file that is fine on the host.
        public static string Describe(string? errorMessage)
        {
            var message = string.IsNullOrWhiteSpace(errorMessage) ? "Backup failed." : errorMessage.Trim();

            var looksLikeMissingMount = message.Contains("not found", StringComparison.OrdinalIgnoreCase)
                || message.Contains("could not find", StringComparison.OrdinalIgnoreCase)
                || message.Contains("no such file", StringComparison.OrdinalIgnoreCase);

            if (!looksLikeMissingMount) return message;

            return message + " If the music or backup share was mounted or remounted on the host after this "
                + "container started, the container still sees the old empty folder - restart the container "
                + "(docker compose restart, or restart the app in TrueNAS), then redo the matches.";
        }
    }
}
