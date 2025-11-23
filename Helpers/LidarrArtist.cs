namespace LidarrCompanion.Helpers
{
    public class LidarrArtist
    {
        public string ArtistName { get; set; } = string.Empty;
        public int Id { get; set; } //foreignArtistId ?
        // Filesystem path for the artist (if provided by the API)
        public string Path { get; set; } = string.Empty;
    }
}
