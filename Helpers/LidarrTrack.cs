namespace LidairrCompanion.Helpers
{
    public class LidarrTrack
    {
        public int Id { get; set; }
        // TrackNumber can be returned as number or string from the API — treat as string
        public string TrackNumber { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public int Duration { get; set; }
        public bool HasFile { get; set; }
    }
}