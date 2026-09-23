using LidarrCompanion.Helpers;

namespace LidarrCompanion.Core.Tests.Helpers
{
    public class PostProcessInvalidationTests
    {
        private static ProposedAction Action(ProposalActionType type, ImportActionStatus status, string release = "Rel",
            string dl = "dl1", string? artist = "Artist") => new()
        {
            Action = type,
            Status = status,
            OriginalRelease = release,
            DownloadId = dl,
            MatchedArtist = artist ?? string.Empty
        };

        [Fact]
        public void ReleasesToInvalidate_IncludesEverySuccessfulActionType()
        {
            var actions = new[]
            {
                Action(ProposalActionType.VerifyImport, ImportActionStatus.Success, "A", "dlA"),
                Action(ProposalActionType.Unlink, ImportActionStatus.Success, "B", "dlB"),
                Action(ProposalActionType.Delete, ImportActionStatus.Success, "C", "dlC"),
                Action(ProposalActionType.MoveToDestination, ImportActionStatus.Success, "D", "dlD"),
            };

            var result = PostProcessInvalidation.ReleasesToInvalidate(actions);

            Assert.Equal(4, result.Count);
            Assert.Contains(new ImplicitUnlink.ReleaseKey("dlA", "A"), result);
            Assert.Contains(new ImplicitUnlink.ReleaseKey("dlD", "D"), result);
        }

        [Fact]
        public void ReleasesToInvalidate_ExcludesActionsThatHaventSettled()
        {
            // A plain Import action never reaches Success under the current status model - it tops
            // out at Sent, with its VerifyImport sibling carrying the real outcome.
            var actions = new[]
            {
                Action(ProposalActionType.Import, ImportActionStatus.Sent, "A"),
                Action(ProposalActionType.VerifyImport, ImportActionStatus.Verifying, "A"),
                Action(ProposalActionType.Import, ImportActionStatus.Failed, "B", "dlB"),
            };

            Assert.Empty(PostProcessInvalidation.ReleasesToInvalidate(actions));
        }

        [Fact]
        public void ReleasesToInvalidate_DedupesMultipleFilesFromTheSameRelease()
        {
            var actions = new[]
            {
                Action(ProposalActionType.VerifyImport, ImportActionStatus.Success, "A", "dlA"),
                Action(ProposalActionType.Unlink, ImportActionStatus.Success, "A", "dlA"),
            };

            Assert.Single(PostProcessInvalidation.ReleasesToInvalidate(actions));
        }

        [Fact]
        public void ArtistsToInvalidate_OnlySuccessfulVerifyImport()
        {
            var actions = new[]
            {
                Action(ProposalActionType.VerifyImport, ImportActionStatus.Success, artist: "Artist A"),
                Action(ProposalActionType.VerifyImport, ImportActionStatus.Failed, artist: "Artist B"),
                Action(ProposalActionType.Import, ImportActionStatus.Sent, artist: "Artist E"), // never reaches Success
                Action(ProposalActionType.Unlink, ImportActionStatus.Success, artist: "Artist C"),
                Action(ProposalActionType.Delete, ImportActionStatus.Success, artist: "Artist D"),
            };

            var result = PostProcessInvalidation.ArtistsToInvalidate(actions);

            Assert.Equal(new[] { "Artist A" }, result);
        }

        [Fact]
        public void ArtistsToInvalidate_DedupesCaseInsensitively()
        {
            var actions = new[]
            {
                Action(ProposalActionType.VerifyImport, ImportActionStatus.Success, artist: "Artist A"),
                Action(ProposalActionType.VerifyImport, ImportActionStatus.Success, artist: "artist a"),
            };

            Assert.Single(PostProcessInvalidation.ArtistsToInvalidate(actions));
        }

        [Fact]
        public void ArtistsToInvalidate_SkipsBlankMatchedArtist()
        {
            var actions = new[] { Action(ProposalActionType.VerifyImport, ImportActionStatus.Success, artist: "") };

            Assert.Empty(PostProcessInvalidation.ArtistsToInvalidate(actions));
        }

        [Fact]
        public void NoProcessedActions_ReturnsEmpty()
        {
            Assert.Empty(PostProcessInvalidation.ReleasesToInvalidate(Array.Empty<ProposedAction>()));
            Assert.Empty(PostProcessInvalidation.ArtistsToInvalidate(Array.Empty<ProposedAction>()));
        }
    }
}
