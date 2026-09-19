namespace LidarrCompanion.Helpers
{
    // When the triage screen's Lidarr data (queue, artists, and everything prefetched from them)
    // is considered too old to keep showing. TriageService is a singleton that used to keep its
    // data for the life of the process - nothing ever expired it - so a page left alone overnight
    // kept showing yesterday's queue.
    public static class RefreshPolicy
    {
        public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromHours(6);

        // Never-loaded counts as stale, so "first landing" and "gone stale" share one code path.
        public static bool IsStale(DateTime? lastLoadedUtc, DateTime nowUtc, TimeSpan maxAge) =>
            lastLoadedUtc is null || nowUtc - lastLoadedUtc.Value >= maxAge;
    }
}
