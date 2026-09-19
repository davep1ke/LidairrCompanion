using LidarrCompanion.Helpers;

namespace LidarrCompanion.Core.Tests.Helpers
{
    public class FileSelectionTests
    {
        private sealed class Item { public required string Name { get; init; } }

        private static List<Item> Items(int n) => Enumerable.Range(0, n).Select(i => new Item { Name = $"f{i}" }).ToList();

        [Fact]
        public void RangeBetween_AnchorBeforeTarget_IsInclusiveAndInListOrder()
        {
            var items = Items(6);

            var range = FileSelection.RangeBetween(items, items[1], items[4]);

            Assert.Equal(new[] { "f1", "f2", "f3", "f4" }, range.Select(i => i.Name));
        }

        [Fact]
        public void RangeBetween_AnchorAfterTarget_StillReturnsListOrder()
        {
            var items = Items(6);

            var range = FileSelection.RangeBetween(items, items[4], items[1]);

            Assert.Equal(new[] { "f1", "f2", "f3", "f4" }, range.Select(i => i.Name));
        }

        [Fact]
        public void RangeBetween_NoAnchor_IsJustTheTarget()
        {
            var items = Items(4);

            var range = FileSelection.RangeBetween(items, null, items[2]);

            Assert.Equal(new[] { "f2" }, range.Select(i => i.Name));
        }

        [Fact]
        public void RangeBetween_AnchorNotInList_IsJustTheTarget()
        {
            var items = Items(4);
            var stranger = new Item { Name = "gone" };

            var range = FileSelection.RangeBetween(items, stranger, items[3]);

            Assert.Equal(new[] { "f3" }, range.Select(i => i.Name));
        }

        [Fact]
        public void RangeBetween_TargetNotInList_IsEmpty()
        {
            var items = Items(3);

            Assert.Empty(FileSelection.RangeBetween(items, items[0], new Item { Name = "gone" }));
        }

        [Fact]
        public void NextUnhandledIndex_SkipsHandledItemsAfterCurrent()
        {
            var handled = new HashSet<int> { 0, 1, 2 };

            Assert.Equal(3, FileSelection.NextUnhandledIndex(5, 1, handled.Contains));
        }

        [Fact]
        public void NextUnhandledIndex_WrapsToEarlierUnhandledItem()
        {
            // 0 was skipped over earlier, 1 and 2 are done, current is the last item.
            var handled = new HashSet<int> { 1, 2 };

            Assert.Equal(0, FileSelection.NextUnhandledIndex(3, 2, handled.Contains));
        }

        [Fact]
        public void NextUnhandledIndex_EverythingHandled_ReturnsMinusOne()
        {
            var handled = new HashSet<int> { 0, 1, 2 };

            Assert.Equal(-1, FileSelection.NextUnhandledIndex(3, 1, handled.Contains));
        }

        [Fact]
        public void NextUnhandledIndex_NoCurrent_StartsFromTheTop()
        {
            var handled = new HashSet<int> { 0 };

            Assert.Equal(1, FileSelection.NextUnhandledIndex(3, -1, handled.Contains));
        }

        [Fact]
        public void NextUnhandledIndex_NeverReturnsTheCurrentItem()
        {
            // Only the current item is unhandled - there's nowhere else to go.
            var handled = new HashSet<int> { 0, 2 };

            Assert.Equal(-1, FileSelection.NextUnhandledIndex(3, 1, handled.Contains));
        }
    }
}
