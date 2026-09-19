using LidarrCompanion.Helpers;

namespace LidarrCompanion.Core.Tests.Helpers
{
    public class AlphabeticalRotationTests
    {
        private static readonly string[] Names = { "Zebra.mp3", "apple.mp3", "Mango.mp3", "banana.mp3", "Zulu.mp3", "1234.mp3", "mint.mp3" };

        [Fact]
        public void StartsAtTheChosenLetter_StaysAlphabetical_AndWrapsToTheStart()
        {
            var result = AlphabeticalRotation.StartingAt(Names, n => n, 'M');

            Assert.Equal(new[] { "Mango.mp3", "mint.mp3", "Zebra.mp3", "Zulu.mp3", "1234.mp3", "apple.mp3", "banana.mp3" }, result);
        }

        [Fact]
        public void StartingAtZ_BeginsWithTheZs()
        {
            var result = AlphabeticalRotation.StartingAt(Names, n => n, 'Z');

            Assert.Equal(new[] { "Zebra.mp3", "Zulu.mp3", "1234.mp3", "apple.mp3", "banana.mp3", "Mango.mp3", "mint.mp3" }, result);
        }

        [Fact]
        public void ALetterWithNoTracks_StartsAtTheNextOneThatHasTracks()
        {
            var result = AlphabeticalRotation.StartingAt(Names, n => n, 'P');

            Assert.Equal("Zebra.mp3", result[0]);
        }

        [Fact]
        public void StartLetterIsCaseInsensitive()
        {
            Assert.Equal(AlphabeticalRotation.StartingAt(Names, n => n, 'M'), AlphabeticalRotation.StartingAt(Names, n => n, 'm'));
        }

        [Fact]
        public void StartingAtA_PutsTheAsFirst_AndDigitNamesAtTheEnd()
        {
            var result = AlphabeticalRotation.StartingAt(Names, n => n, 'A');

            Assert.Equal(new[] { "apple.mp3", "banana.mp3", "Mango.mp3", "mint.mp3", "Zebra.mp3", "Zulu.mp3", "1234.mp3" }, result);
        }

        [Fact]
        public void NothingReachingTheStartLetter_FallsBackToPlainOrder()
        {
            var result = AlphabeticalRotation.StartingAt(new[] { "b.mp3", "a.mp3" }, n => n, 'Z');

            Assert.Equal(new[] { "a.mp3", "b.mp3" }, result);
        }

        [Fact]
        public void EveryItemIsKeptExactlyOnce()
        {
            var result = AlphabeticalRotation.StartingAt(Names, n => n, 'N');

            Assert.Equal(Names.OrderBy(n => n), result.OrderBy(n => n));
        }

        [Fact]
        public void PickedLetterIsAlwaysOneThatHasTracks()
        {
            var random = new Random(1);
            var present = new HashSet<char> { 'A', 'B', 'M', 'Z' };

            for (var i = 0; i < 200; i++)
                Assert.Contains(AlphabeticalRotation.PickStartLetter(Names, n => n, random)!.Value, present);
        }

        [Fact]
        public void EveryPresentLetterCanBePicked()
        {
            var random = new Random(2);
            var seen = Enumerable.Range(0, 500).Select(_ => AlphabeticalRotation.PickStartLetter(Names, n => n, random)!.Value).ToHashSet();

            Assert.Equal(new HashSet<char> { 'A', 'B', 'M', 'Z' }, seen);
        }

        [Fact]
        public void NoLetterInitials_MeansNothingToPick()
        {
            Assert.Null(AlphabeticalRotation.PickStartLetter(new[] { "1.mp3", "_x.mp3" }, n => n, new Random()));
            Assert.Null(AlphabeticalRotation.PickStartLetter(Array.Empty<string>(), n => n, new Random()));
        }
    }
}
