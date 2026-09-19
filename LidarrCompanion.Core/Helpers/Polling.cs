namespace LidarrCompanion.Helpers
{
    public static class Polling
    {
        // Calls fetch up to maxAttempts times, waiting delay between attempts, and returns the
        // first result isDone accepts - or the last result fetched if none did (so callers can
        // still show "nothing yet" rather than getting a null). Used to wait for Lidarr to finish
        // its own asynchronous metadata refresh after an artist is created: the artist exists
        // immediately, its albums appear some seconds later.
        //
        // A fetch that throws is treated as "not ready" and retried; only the final attempt's
        // exception propagates, so a transient failure mid-wait doesn't abort the whole wait.
        public static async Task<T> UntilAsync<T>(
            Func<Task<T>> fetch,
            Func<T, bool> isDone,
            int maxAttempts,
            TimeSpan delay,
            CancellationToken cancellationToken = default)
        {
            if (maxAttempts < 1) throw new ArgumentOutOfRangeException(nameof(maxAttempts));

            T? last = default;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    last = await fetch();
                    if (isDone(last)) return last;
                }
                catch (Exception) when (attempt < maxAttempts && !cancellationToken.IsCancellationRequested)
                {
                    // Not ready yet - fall through to the delay and try again.
                }

                if (attempt < maxAttempts)
                    await Task.Delay(delay, cancellationToken);
            }

            return last!;
        }
    }
}
