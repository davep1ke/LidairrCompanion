using LidarrCompanion.Helpers;

namespace LidarrCompanion.Core.Tests.Helpers
{
    public class PollingTests
    {
        private static readonly TimeSpan NoDelay = TimeSpan.Zero;

        [Fact]
        public async Task ReturnsAsSoonAsTheResultIsAcceptable()
        {
            var calls = 0;

            var result = await Polling.UntilAsync(() => Task.FromResult(++calls), n => n >= 3, maxAttempts: 10, NoDelay);

            Assert.Equal(3, result);
            Assert.Equal(3, calls);
        }

        [Fact]
        public async Task GivesUpAfterMaxAttempts_ReturningTheLastResult()
        {
            var calls = 0;

            var result = await Polling.UntilAsync(() => Task.FromResult(++calls), _ => false, maxAttempts: 4, NoDelay);

            Assert.Equal(4, result);
            Assert.Equal(4, calls);
        }

        [Fact]
        public async Task ATransientFailureIsRetried()
        {
            var calls = 0;

            var result = await Polling.UntilAsync(() =>
            {
                calls++;
                if (calls < 3) throw new InvalidOperationException("Lidarr not ready");
                return Task.FromResult("ok");
            }, s => s == "ok", maxAttempts: 5, NoDelay);

            Assert.Equal("ok", result);
            Assert.Equal(3, calls);
        }

        [Fact]
        public async Task TheFinalAttemptsExceptionPropagates()
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Polling.UntilAsync<int>(() => throw new InvalidOperationException("down"), _ => true, maxAttempts: 2, NoDelay));
        }

        [Fact]
        public async Task CancellationStopsTheWait()
        {
            using var cts = new CancellationTokenSource();
            var calls = 0;

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                Polling.UntilAsync(() =>
                {
                    if (++calls == 2) cts.Cancel();
                    return Task.FromResult(calls);
                }, _ => false, maxAttempts: 10, TimeSpan.FromMilliseconds(5), cts.Token));

            Assert.True(calls <= 3);
        }

        [Fact]
        public async Task ZeroAttemptsIsRejected()
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                Polling.UntilAsync(() => Task.FromResult(1), _ => true, maxAttempts: 0, NoDelay));
        }
    }
}
