namespace LidairrCompanion.Helpers
{
    using System.Collections.Generic;

    public class LidarrAlbum
    {
        public string Title { get; set; } = string.Empty;
        public int Id { get; set; }
        public List<string> ImageUrls { get; set; } = new();
        public List<LidarrAlbumRelease> Releases { get; set; } = new();
    }
}