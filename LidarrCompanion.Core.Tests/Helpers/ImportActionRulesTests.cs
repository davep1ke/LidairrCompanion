using LidarrCompanion.Helpers;

namespace LidarrCompanion.Core.Tests.Helpers
{
    public class ImportActionRulesTests
    {
        [Theory]
        [InlineData(ProposalActionType.Import, true)]
        [InlineData(ProposalActionType.Unlink, true)]
        [InlineData(ProposalActionType.Delete, true)]
        [InlineData(ProposalActionType.MoveToDestination, true)]
        [InlineData(ProposalActionType.VerifyImport, false)]
        [InlineData(ProposalActionType.NotForImport, false)]
        [InlineData(ProposalActionType.Defer, false)]
        public void RequiresSourceFile_MatchesWhetherTheActionStillTouchesTheOriginalFile(ProposalActionType type, bool expected)
        {
            Assert.Equal(expected, ImportActionRules.RequiresSourceFile(type));
        }
    }
}
