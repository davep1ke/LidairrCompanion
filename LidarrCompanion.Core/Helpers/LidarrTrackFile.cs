namespace LidarrCompanion.Helpers
{
    // Represents a file record returned by Lidarr for an imported track
    public class LidarrTrackFile
    {
        public int Id { get; set; }
        public string Path { get; set; } = string.Empty;
    }
}
