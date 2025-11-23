namespace LidarrCompanion.Helpers
{
    public class LidarrManualFileQuality
    {
        public LidarrQualityInfo? quality { get; set; }
        public LidarrRevision? revision { get; set; }
    }

    public class LidarrQualityInfo
    {
        public int id { get; set; }
        public string? name { get; set; }
    }

    public class LidarrRevision
    {
        public int version { get; set; }
        public int real { get; set; }
        public bool isRepack { get; set; }
    }
}
