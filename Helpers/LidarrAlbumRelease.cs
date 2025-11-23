namespace LidarrCompanion.Helpers
{
    public class LidarrAlbumRelease
    {
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Format { get; set; } = string.Empty;
        public string Country { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string PublishDate { get; set; } = string.Empty;
        // Filesystem path for this release (if provided by the API)
        public string Path { get; set; } = string.Empty;

        public string GetDisplayText()
        {
            // Build suffix from country and format (e.g. "US/CD" or "CD")
            var parts = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrWhiteSpace(Country)) parts.Add(Country);
            if (!string.IsNullOrWhiteSpace(Format)) parts.Add(Format);

            var suffix = parts.Count > 0 ? string.Join('/', parts) : string.Empty;

            var baseTitle = string.IsNullOrWhiteSpace(PublishDate) ? Title : $"{Title} ({PublishDate})";

            return string.IsNullOrWhiteSpace(suffix) ? baseTitle : $"{baseTitle} - {suffix}";
        }
    }
}