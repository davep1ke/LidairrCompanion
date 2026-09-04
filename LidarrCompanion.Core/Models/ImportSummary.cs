namespace LidarrCompanion.Models
{
    // Three separate tallies, not one blended "success" count - a single Move or real Lidarr
    // import can also trigger a secondary copy, and folding all of that into one number is what
    // made "2 files imported" read as "4 successes" in the original bug this record fixed.
    public record ImportSummary(int Imported, int Moved, int Copied, int Failed)
    {
        public bool HasAnyResult => Imported + Moved + Copied + Failed > 0;

        public string BuildMessage()
        {
            var parts = new List<string>();
            if (Imported > 0) parts.Add($"{Imported} imported via Lidarr");
            if (Moved > 0) parts.Add($"{Moved} moved to a destination");
            if (Copied > 0) parts.Add($"{Copied} secondary {(Copied == 1 ? "copy" : "copies")} made");

            var summary = parts.Count > 0 ? string.Join(", ", parts) : "nothing to report";

            return Failed > 0
                ? $"Import completed: {summary}. {Failed} failed - failed actions remain in the list, click Process Actions again to retry."
                : $"Import completed: {summary}.";
        }
    }
}
