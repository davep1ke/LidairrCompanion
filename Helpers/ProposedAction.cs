namespace LidarrCompanion.Helpers
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

        // New fields required for import
        public int ArtistId { get; set; }
        public int AlbumId { get; set; }
        public int AlbumReleaseId { get; set; }
        // Filesystem path to use for import (album or release folder)
        public string Path { get; set; } = string.Empty;
        public string DownloadId { get; set; } = string.Empty;
        // Quality information for the file (quality + revision) as returned by Lidarr
        public LidarrManualFileQuality? Quality { get; set; }

        // New: indicate this proposed action is a move to NotSelectedPath rather than import
        public bool IsMoveToNotSelected { get; set; }
        public string MoveDestinationPath { get; set; } = string.Empty;

        // New: explicitly mark a file as NotForImport (user requested). Different UI highlight.
        public bool IsNotForImport { get; set; }

        // New: human-readable action type for UI (e.g. "Import" or "Move to Not Import")
        public string ActionType { get; set; } = "Import";
    }
}