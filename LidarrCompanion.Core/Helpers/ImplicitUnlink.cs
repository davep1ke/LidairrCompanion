namespace LidarrCompanion.Helpers
{
    // The "anything without an action is treated as Unlink" rule, applied when processing starts.
    //
    // Why it exists: once Lidarr imports from a release's folder it throws away whatever files are
    // left unmatched in there. A file the user never made a decision about would be deleted, so it
    // has to be moved out first - which is exactly what an Unlink action does.
    //
    // Scope is deliberately only releases that have at least one Import action. Lidarr's clean-up
    // happens as part of an import, so a release with no import isn't at risk, and moving files the
    // user simply hasn't got to yet (say, after a lone Delete) would be a nasty surprise.
    public static class ImplicitUnlink
    {
        public readonly record struct ReleaseKey(string DownloadId, string OriginalRelease);

        public static List<ReleaseKey> ReleasesBeingImported(IEnumerable<ProposedAction> actions) =>
            actions
                .Where(a => a.Action == ProposalActionType.Import && !a.IsImplicitUnlink)
                .Select(a => new ReleaseKey(a.DownloadId ?? string.Empty, a.OriginalRelease ?? string.Empty))
                .Distinct()
                .ToList();

        // A queue record is the same release as an action when their download ids agree; when either
        // side has no download id, fall back to the release title.
        public static bool IsSameRelease(string? recordDownloadId, string? recordTitle, ReleaseKey key)
        {
            if (!string.IsNullOrWhiteSpace(recordDownloadId) && !string.IsNullOrWhiteSpace(key.DownloadId))
                return string.Equals(recordDownloadId, key.DownloadId, StringComparison.Ordinal);

            return !string.IsNullOrWhiteSpace(recordTitle)
                && string.Equals(recordTitle, key.OriginalRelease, StringComparison.Ordinal);
        }

        // Unlink proposals for every file in the release that has no action yet. fileExists guards
        // against files that are already gone (moved by an earlier run, or imported by Lidarr) -
        // unlinking those would fail the whole run on "source file not found".
        public static List<ProposedAction> Build(
            IEnumerable<LidarrManualImportFile> releaseFiles,
            IEnumerable<ProposedAction> existingActions,
            string originalRelease,
            string downloadId,
            Func<LidarrManualImportFile, bool> fileExists)
        {
            var actioned = existingActions.Select(a => a.FileId).ToHashSet();
            var result = new List<ProposedAction>();

            foreach (var file in releaseFiles)
            {
                if (actioned.Contains(file.Id) || !fileExists(file)) continue;

                result.Add(new ProposedAction
                {
                    Action = ProposalActionType.Unlink,
                    FileId = file.Id,
                    Path = file.Path,
                    OriginalFileName = Path.GetFileName(file.Path) ?? file.Name,
                    OriginalRelease = originalRelease,
                    DownloadId = downloadId,
                    Quality = file.Quality,
                    IsImplicitUnlink = true
                });
            }

            return result;
        }
    }
}
