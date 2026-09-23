using LidarrCompanion.Helpers;

namespace LidarrCompanion.Core.Tests.Helpers
{
    public class PostProcessInvalidationTests
    {
        private static ProposedAction Action(ProposalActionType type, string status, string release = "Rel",
            string dl = "dl1", string? artist = "Artist") => new()
        {
            Action = type,
            ImportStatus = status,
            OriginalRelease = release,
            DownloadId = dl,
            MatchedArtist = artist ?? string.Empty
        };

        [Fact]
        public void ReleasesToInvalidate_IncludesEverySuccessfulActionType_NotJustImport()
        {
            var actions = new[]
            {
                Action(ProposalActionType.Import, "Success", "A", "dlA"),
                Action(ProposalActionType.Unlink, "Success", "B", "dlB"),
                Action(ProposalActionType.Delete, "Success", "C", "dlC"),
                Action(ProposalActionType.MoveToDestination, "Success", "D", "dlD"),
            };

            var result = PostProcessInvalidation.ReleasesToInvalidate(actions);

            Assert.Equal(4, result.Count);
            Assert.Contains(new ImplicitUnlink.ReleaseKey("dlA", "A"), result);
            Assert.Contains(new ImplicitUnlink.ReleaseKey("dlD", "D"), result);
        }

        [Fact]
        public void ReleasesToInvalidate_ExcludesFailedActions()
        {
            var actions = new[]
            {
                Action(ProposalActionType.Import, "Success", "A"),
                Action(ProposalActionType.Import, "Failed", "B", "dlB"),
            };

            var result = PostProcessInvalidation.ReleasesToInvalidate(actions);

            Assert.Single(result);
            Assert.Equal("A", result[0].OriginalRelease);
        }

        [Fact]
        public void ReleasesToInvalidate_DedupesMultipleFilesFromTheSameRelease()
        {
            var actions = new[]
            {
                Action(ProposalActionType.Import, "Success", "A", "dlA"),
                Action(ProposalActionType.Unlink, "Success", "A", "dlA"),
            };

            Assert.Single(PostProcessInvalidation.ReleasesToInvalidate(actions));
        }

        [Fact]
        public void ArtistsToInvalidate_OnlySuccessfulImports()
        {
            var actions = new[]
            {
                Action(ProposalActionType.Import, "Success", artist: "Artist A"),
                Action(ProposalActionType.Import, "Failed", artist: "Artist B"),
                Action(ProposalActionType.Unlink, "Success", artist: "Artist C"),
                Action(ProposalActionType.Delete, "Success", artist: "Artist D"),
            };

            var result = PostProcessInvalidation.ArtistsToInvalidate(actions);

            Assert.Equal(new[] { "Artist A" }, result);
        }

        [Fact]
        public void ArtistsToInvalidate_DedupesCaseInsensitively()
        {
            var actions = new[]
            {
                Action(ProposalActionType.Import, "Success", artist: "Artist A"),
                Action(ProposalActionType.Import, "Success", artist: "artist a"),
            };

            Assert.Single(PostProcessInvalidation.ArtistsToInvalidate(actions));
        }

        [Fact]
        public void ArtistsToInvalidate_SkipsBlankMatchedArtist()
        {
            var actions = new[] { Action(ProposalActionType.Import, "Success", artist: "") };

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
