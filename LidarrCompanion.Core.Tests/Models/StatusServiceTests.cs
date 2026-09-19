using LidarrCompanion.Models;

namespace LidarrCompanion.Core.Tests.Models
{
    // Timing-based, so each test uses a tiny AutoDismissAfter and generously long waits - the
    // assertions are about "did/didn't clear", never about how quickly.
    public class StatusServiceTests
    {
        private static StatusService NewService(int dismissMs = 60) => new() { AutoDismissAfter = TimeSpan.FromMilliseconds(dismissMs) };

        [Fact]
        public async Task Info_ClearsItselfAfterTheDelay_AndRaisesChanged()
        {
            var status = NewService();
            var changes = 0;
            status.Changed += () => Interlocked.Increment(ref changes);

            status.SetInfo("Refreshed");
            Assert.Equal("Refreshed", status.Message);

            await Task.Delay(500);

            Assert.Null(status.Message);
            Assert.Equal(StatusKind.Idle, status.Kind);
            Assert.Equal(2, changes); // once on set, once on auto-dismiss
        }

        [Fact]
        public async Task Error_AlsoClearsItself()
        {
            var status = NewService();

            status.ShowError("Boom");
            await Task.Delay(500);

            Assert.Null(status.Message);
        }

        [Fact]
        public async Task Busy_IsNeverAutoDismissed()
        {
            var status = NewService();

            status.SetBusy("Importing...");
            await Task.Delay(500);

            Assert.True(status.IsBusy);
            Assert.Equal("Importing...", status.Message);
        }

        [Fact]
        public async Task AnOlderNoticesTimer_DoesNotWipeANewerMessage()
        {
            var status = NewService(dismissMs: 200);

            status.SetInfo("first");
            await Task.Delay(120);          // first's timer still has ~80ms to run
            status.SetInfo("second");       // restarts the clock for the visible message
            await Task.Delay(150);          // first's timer has now fired, second's has not

            Assert.Equal("second", status.Message);

            await Task.Delay(400);
            Assert.Null(status.Message);
        }

        [Fact]
        public async Task ClearBusy_OnlyClearsBusy_NotAnInfoNoticeThatReplacedIt()
        {
            var status = NewService(dismissMs: 5000);

            status.SetBusy("Working...");
            status.SetInfo("Done: 3 imported");
            status.ClearBusy();

            Assert.Equal("Done: 3 imported", status.Message);
            await Task.CompletedTask;
        }

        [Fact]
        public void DefaultAutoDismiss_IsTwoMinutes()
        {
            Assert.Equal(TimeSpan.FromMinutes(2), new StatusService().AutoDismissAfter);
        }
    }
}
