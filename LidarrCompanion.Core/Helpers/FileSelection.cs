namespace LidarrCompanion.Helpers
{
    // Pure list-selection maths for the triage screen's "Unimported Release Files" table, kept out
    // of the Blazor page/TriageService so it can be tested directly.
    public static class FileSelection
    {
        // Shift-click range: everything between the anchor and the clicked item, inclusive, in
        // list order regardless of which of the two comes first. If the anchor isn't in the list
        // (nothing selected yet, or it was removed) the range is just the clicked item.
        public static List<T> RangeBetween<T>(IReadOnlyList<T> items, T? anchor, T target) where T : class
        {
            var targetIndex = IndexOf(items, target);
            if (targetIndex < 0) return new List<T>();

            var anchorIndex = anchor is null ? -1 : IndexOf(items, anchor);
            if (anchorIndex < 0) return new List<T> { target };

            var (from, to) = anchorIndex <= targetIndex ? (anchorIndex, targetIndex) : (targetIndex, anchorIndex);
            return items.Skip(from).Take(to - from + 1).ToList();
        }

        // After acting on a file, where should selection go? The next not-yet-handled item after
        // the current one; failing that, the first not-yet-handled item anywhere (so earlier files
        // that were skipped over aren't forgotten); -1 when everything has been handled.
        public static int NextUnhandledIndex(int count, int currentIndex, Func<int, bool> isHandled)
        {
            for (var i = currentIndex + 1; i < count; i++)
                if (!isHandled(i)) return i;

            for (var i = 0; i < count && i <= currentIndex; i++)
                if (i != currentIndex && !isHandled(i)) return i;

            return -1;
        }

        // Has the user made a decision about this file? An auto-generated Unlink (see
        // ProposedAction.IsAutoUnlink) is only a default for the leftovers of a release, so it does
        // NOT count - otherwise matching one track marks every sibling "handled" and selection
        // skips straight past the rest of the release to the next artist.
        public static bool IsUserHandled(bool isAssigned, bool hasProposal, bool proposalIsAutoUnlink) =>
            isAssigned || (hasProposal && !proposalIsAutoUnlink);

        private static int IndexOf<T>(IReadOnlyList<T> items, T item) where T : class
        {
            for (var i = 0; i < items.Count; i++)
                if (ReferenceEquals(items[i], item)) return i;
            return -1;
        }
    }
}
