using LidarrCompanion.Helpers;

namespace LidarrCompanion.Core.Tests.Helpers
{
    public class ImplicitUnlinkTests
    {
        private static LidarrManualImportFile File(int id, string path = "") =>
            new() { Id = id, Name = $"f{id}", Path = string.IsNullOrEmpty(path) ? $"/import/Rel/f{id}.mp3" : path };

        private static ProposedAction Action(ProposalActionType type, int fileId, string release = "Rel", string dl = "dl1", bool implicitUnlink = false) =>
            new() { Action = type, FileId = fileId, OriginalRelease = release, DownloadId = dl, IsImplicitUnlink = implicitUnlink };

        [Fact]
        public void OnlyFilesWithoutAnyAction_GetAnUnlink()
        {
            var files = new[] { File(1), File(2), File(3), File(4) };
            var actions = new[] { Action(ProposalActionType.Import, 1), Action(ProposalActionType.Delete, 2) };

            var result = ImplicitUnlink.Build(files, actions, "Rel", "dl1", _ => true);

            Assert.Equal(new[] { 3, 4 }, result.Select(a => a.FileId));
            Assert.All(result, a =>
            {
                Assert.Equal(ProposalActionType.Unlink, a.Action);
                Assert.True(a.IsImplicitUnlink);
                Assert.Equal("Rel", a.OriginalRelease);
                Assert.Equal("dl1", a.DownloadId);
            });
        }

        [Fact]
        public void FilesThatNoLongerExist_AreSkipped()
        {
            var files = new[] { File(1), File(2), File(3) };

            var result = ImplicitUnlink.Build(files, Array.Empty<ProposedAction>(), "Rel", "dl1", f => f.Id != 2);

            Assert.Equal(new[] { 1, 3 }, result.Select(a => a.FileId));
        }

        [Fact]
        public void UsesTheFileNameFromThePath()
        {
            var result = ImplicitUnlink.Build(new[] { File(1, "/import/Rel/Some Track.mp3") }, Array.Empty<ProposedAction>(), "Rel", "dl1", _ => true);

            Assert.Equal("Some Track.mp3", result.Single().OriginalFileName);
        }

        [Fact]
        public void OnlyReleasesWithAnImportAction_AreInScope()
        {
            var actions = new[]
            {
                Action(ProposalActionType.Import, 1, "A", "dlA"),
                Action(ProposalActionType.Import, 2, "A", "dlA"),
                Action(ProposalActionType.Delete, 3, "B", "dlB"),
                Action(ProposalActionType.MoveToDestination, 4, "C", "dlC"),
            };

            var releases = ImplicitUnlink.ReleasesBeingImported(actions);

            Assert.Equal(new[] { new ImplicitUnlink.ReleaseKey("dlA", "A") }, releases);
        }

        [Fact]
        public void LeftoverImplicitUnlinks_DontCountAsTheReasonToProcessARelease()
        {
            var actions = new[] { Action(ProposalActionType.Unlink, 1, "A", "dlA", implicitUnlink: true) };

            Assert.Empty(ImplicitUnlink.ReleasesBeingImported(actions));
        }

        [Theory]
        [InlineData("dl1", "Title", "dl1", "Other", true)]    // same download id wins over a differing title
        [InlineData("dl1", "Title", "dl2", "Title", false)]   // different download ids: different releases even if titles match
        [InlineData("", "Title", "dl1", "Title", true)]       // no id on the record: fall back to the title
        [InlineData("dl1", "Title", "", "Title", true)]       // no id on the action: fall back to the title
        [InlineData("", "", "", "", false)]                   // nothing to match on
        public void IsSameRelease_PrefersDownloadId_ThenTitle(string recordDl, string recordTitle, string keyDl, string keyTitle, bool expected)
        {
            Assert.Equal(expected, ImplicitUnlink.IsSameRelease(recordDl, recordTitle, new ImplicitUnlink.ReleaseKey(keyDl, keyTitle)));
        }
    }
}
