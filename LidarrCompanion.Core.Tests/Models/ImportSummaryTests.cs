using LidarrCompanion.Models;

namespace LidarrCompanion.Core.Tests.Models
{
    public class ImportSummaryTests
    {
        [Fact]
        public void BuildMessage_TalliesEachKindSeparately_NotAsOneBlendedCount()
        {
            // Regression test for the real bug this record fixed: a single Move that also
            // triggers a secondary copy must read as "1 moved, 1 copy", never "2 successes".
            var summary = new ImportSummary(Imported: 0, Moved: 1, Copied: 1, Failed: 0);

            Assert.Equal("Import completed: 1 moved to a destination, 1 secondary copy made.", summary.BuildMessage());
        }

        [Fact]
        public void BuildMessage_PluralizesCopies()
        {
            var summary = new ImportSummary(0, 0, 2, 0);

            Assert.Equal("Import completed: 2 secondary copies made.", summary.BuildMessage());
        }

        [Fact]
        public void BuildMessage_AllThreeKinds_JoinsInOrder()
        {
            var summary = new ImportSummary(Imported: 2, Moved: 1, Copied: 1, Failed: 0);

            Assert.Equal(
                "Import completed: 2 imported via Lidarr, 1 moved to a destination, 1 secondary copy made.",
                summary.BuildMessage());
        }

        [Fact]
        public void BuildMessage_NoSuccesses_ReadsAsNothingToReport()
        {
            var summary = new ImportSummary(0, 0, 0, 0);

            Assert.Equal("Import completed: nothing to report.", summary.BuildMessage());
        }

        [Fact]
        public void BuildMessage_WithFailures_AppendsRetryHint()
        {
            var summary = new ImportSummary(Imported: 1, Moved: 0, Copied: 0, Failed: 2);

            Assert.Equal(
                "Import completed: 1 imported via Lidarr. 2 failed - failed actions remain in the list, click Process Actions again to retry.",
                summary.BuildMessage());
        }

        [Theory]
        [InlineData(0, 0, 0, 0, false)]
        [InlineData(1, 0, 0, 0, true)]
        [InlineData(0, 0, 0, 1, true)]
        public void HasAnyResult_TrueWhenAnyTallyIsNonZero(int imported, int moved, int copied, int failed, bool expected)
        {
            var summary = new ImportSummary(imported, moved, copied, failed);

            Assert.Equal(expected, summary.HasAnyResult);
        }
    }
}
