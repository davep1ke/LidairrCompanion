namespace LidarrCompanion.Helpers
{
    // What needs a fresh fetch after Process Actions (or a background VerifyImport settling) runs,
    // computed purely from the processed ProposedActions (Status already set by the pipeline) - no
    // I/O here, PrefetchService's invalidation calls are the I/O-adjacent part.
    public static class PostProcessInvalidation
    {
        // Any successful action changed what's actually on disk in that release's folder, so the
        // cached file list for it is stale regardless of which action type ran. A plain Import
        // action itself never reaches Success under the current status model - it tops out at Sent,
        // with its VerifyImport sibling reaching Success once Lidarr actually confirms the file
        // landed (which is also when it actually leaves the source folder) - so this naturally
        // covers confirmed imports too, not just synchronous Unlink/Delete/Move.
        public static List<ImplicitUnlink.ReleaseKey> ReleasesToInvalidate(IEnumerable<ProposedAction> processedActions) =>
            processedActions
                .Where(a => a.Status == ImportActionStatus.Success)
                .Select(a => new ImplicitUnlink.ReleaseKey(a.DownloadId ?? string.Empty, a.OriginalRelease ?? string.Empty))
                .Distinct()
                .ToList();

        // Only a successful VerifyImport (Lidarr-confirmed) flips a track's HasFile in Lidarr's own
        // data - Unlink/Delete/Move don't touch Lidarr's album/track metadata, and a plain Import
        // action's own Success doesn't mean confirmed (it never reaches Success at all now, only
        // Sent - see above), so this checks the VerifyImport sibling specifically.
        public static List<string> ArtistsToInvalidate(IEnumerable<ProposedAction> processedActions) =>
            processedActions
                .Where(a => a.Action == ProposalActionType.VerifyImport
                    && a.Status == ImportActionStatus.Success
                    && !string.IsNullOrWhiteSpace(a.MatchedArtist))
                .Select(a => a.MatchedArtist!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
    }
}
