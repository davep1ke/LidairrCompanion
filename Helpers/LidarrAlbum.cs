namespace LidarrCompanion.Helpers
{
    using System.Collections.Generic;

    public class LidarrAlbum
    {
        public string Title { get; set; } = string.Empty;
        public int Id { get; set; }
        public string Path { get; set; } = string.Empty; // filesystem path for album if provided
        public List<string> ImageUrls { get; set; } = new();
        public List<LidarrAlbumRelease> Releases { get; set; } = new();

        // New: album type as returned by the API (e.g., "album", "single", etc.)
        public string AlbumType { get; set; } = string.Empty;
    }
}