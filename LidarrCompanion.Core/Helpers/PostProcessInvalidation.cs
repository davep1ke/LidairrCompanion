namespace LidarrCompanion.Helpers
{
    // What needs a fresh fetch after Process Actions runs, computed purely from the processed
    // ProposedActions (ImportStatus already set by the pipeline) - no I/O here, PrefetchService's
    // invalidation calls are the I/O-adjacent part.
    public static class PostProcessInvalidation
    {
        // Any successful action (Import/Unlink/Delete/Move) changed what's actually on disk in that
        // release's folder, so the cached file list for it is stale regardless of which action type
        // ran - not just Import.
        public static List<ImplicitUnlink.ReleaseKey> ReleasesToInvalidate(IEnumerable<ProposedAction> processedActions) =>
            processedActions
                .Where(a => string.Equals(a.ImportStatus, "Success", StringComparison.OrdinalIgnoreCase))
                .Select(a => new ImplicitUnlink.ReleaseKey(a.DownloadId ?? string.Empty, a.OriginalRelease ?? string.Empty))
                .Distinct()
                .ToList();

        // Only a successful Import flips a track's HasFile in Lidarr's own data - Unlink/Delete/Move
        // don't touch Lidarr's album/track metadata, so they don't stale the artist-tracks cache.
        public static List<string> ArtistsToInvalidate(IEnumerable<ProposedAction> processedActions) =>
            processedActions
                .Where(a => a.Action == ProposalActionType.Import
                    && string.Equals(a.ImportStatus, "Success", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(a.MatchedArtist))
                .Select(a => a.MatchedArtist!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
    }
}
