namespace LidairrCompanion.Helpers
{
 public class ProposedAction
 {
 public string OriginalFileName { get; set; } = string.Empty;
 public string OriginalRelease { get; set; } = string.Empty;
 public string MatchedArtist { get; set; } = string.Empty;
 public string MatchedTrack { get; set; } = string.Empty;
 public string MatchedRelease { get; set; } = string.Empty;
 public int TrackId { get; set; }
 public int FileId { get; set; }
 }
}