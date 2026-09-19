using LidarrCompanion.Helpers;

namespace LidarrCompanion.Core.Tests.Helpers
{
    public class RefreshPolicyTests
    {
        private static readonly DateTime Now = new(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void NeverLoaded_IsStale()
        {
            Assert.True(RefreshPolicy.IsStale(null, Now, RefreshPolicy.DefaultMaxAge));
        }

        [Fact]
        public void LoadedRecently_IsNotStale()
        {
            Assert.False(RefreshPolicy.IsStale(Now.AddMinutes(-10), Now, RefreshPolicy.DefaultMaxAge));
        }

        [Fact]
        public void JustUnderTheLimit_IsNotStale_ExactlyAtTheLimit_IsStale()
        {
            Assert.False(RefreshPolicy.IsStale(Now - RefreshPolicy.DefaultMaxAge + TimeSpan.FromSeconds(1), Now, RefreshPolicy.DefaultMaxAge));
            Assert.True(RefreshPolicy.IsStale(Now - RefreshPolicy.DefaultMaxAge, Now, RefreshPolicy.DefaultMaxAge));
        }

        [Fact]
        public void DefaultMaxAge_IsSixHours()
        {
            Assert.Equal(TimeSpan.FromHours(6), RefreshPolicy.DefaultMaxAge);
        }
    }
}
