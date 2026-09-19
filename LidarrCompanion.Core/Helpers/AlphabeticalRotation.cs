namespace LidarrCompanion.Helpers
{
    // Keeps a list alphabetical but lets it start part-way through: the items come out in normal
    // A-Z order beginning at a chosen letter, then wrap round to the start. Used by Sift so that a
    // queue that's rarely cleared doesn't leave the tail of the alphabet (Z...) waiting forever
    // behind everything that sorts before it.
    public static class AlphabeticalRotation
    {
        private static bool IsAsciiLetter(char c) => c is >= 'A' and <= 'Z';

        private static char? Initial(string? name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var c = char.ToUpperInvariant(name[0]);
            return IsAsciiLetter(c) ? c : null;
        }

        // A random letter, chosen uniformly from the initials that actually occur (so a letter with
        // no tracks can never be picked, and a sparse letter is as likely as a crowded one). Null
        // when no name starts with a letter, i.e. there is nothing to rotate.
        public static char? PickStartLetter<T>(IEnumerable<T> items, Func<T, string> name, Random random)
        {
            var present = items.Select(i => Initial(name(i))).Where(c => c.HasValue).Select(c => c!.Value).Distinct().OrderBy(c => c).ToList();
            return present.Count == 0 ? null : present[random.Next(present.Count)];
        }

        // Alphabetical (case-insensitive) starting from the first item whose initial is startLetter
        // or later, then wrapping to whatever sorts before it. Names that don't start with a letter
        // (digits, symbols) sort before A, so they land at the end of the wrap. If nothing reaches
        // startLetter the plain alphabetical order is returned.
        public static List<T> StartingAt<T>(IEnumerable<T> items, Func<T, string> name, char startLetter)
        {
            var ordered = items.OrderBy(name, StringComparer.OrdinalIgnoreCase).ToList();
            var start = char.ToUpperInvariant(startLetter);

            var index = ordered.FindIndex(i => Initial(name(i)) is { } c && c >= start);
            if (index <= 0) return ordered;

            return ordered.Skip(index).Concat(ordered.Take(index)).ToList();
        }
    }
}
