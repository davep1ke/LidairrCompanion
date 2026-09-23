using LidarrCompanion.Helpers;

namespace LidarrCompanion.Core.Tests.Helpers
{
    public class ImportActionDisplayTests
    {
        private static ProposedAction Action(ImportActionStatus status, int retry = 0, int max = 0, string? error = null) => new()
        {
            Status = status,
            RetryCount = retry,
            MaxRetries = max,
            ErrorMessage = error ?? string.Empty
        };

        [Fact]
        public void Verifying_ShowsRetryProgress()
        {
            Assert.Equal("Verifying (3/30)...", ImportActionDisplay.Describe(Action(ImportActionStatus.Verifying, 3, 30)));
        }

        [Fact]
        public void Verifying_WithNoMaxRetries_OmitsTheCounter()
        {
            Assert.Equal("Verifying...", ImportActionDisplay.Describe(Action(ImportActionStatus.Verifying)));
        }

        [Fact]
        public void Failed_ShowsTheErrorMessageWhenPresent()
        {
            Assert.Equal("Could not verify import after 30 attempts.",
                ImportActionDisplay.Describe(Action(ImportActionStatus.Failed, error: "Could not verify import after 30 attempts.")));
        }

        [Fact]
        public void Failed_WithNoErrorMessage_FallsBackToAGenericLabel()
        {
            Assert.Equal("Failed", ImportActionDisplay.Describe(Action(ImportActionStatus.Failed)));
        }

        [Theory]
        [InlineData(ImportActionStatus.Pending, "Pending")]
        [InlineData(ImportActionStatus.Sent, "Sent to Lidarr")]
        [InlineData(ImportActionStatus.Success, "Success")]
        public void OtherStatuses_HaveFixedLabels(ImportActionStatus status, string expected)
        {
            Assert.Equal(expected, ImportActionDisplay.Describe(Action(status)));
        }
    }
}
