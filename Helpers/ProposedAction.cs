using System.ComponentModel;
using System.Windows.Media;

namespace LidarrCompanion.Helpers
{
    public class ProposedAction : INotifyPropertyChanged
    {
        public string OriginalFileName { get; set; } = string.Empty;
        public string OriginalRelease { get; set; } = string.Empty;
        public string MatchedArtist { get; set; } = string.Empty;

        private string _matchedTrack = string.Empty;
        public string MatchedTrack { get => _matchedTrack; set { if (_matchedTrack != value) { _matchedTrack = value; OnPropertyChanged(nameof(MatchedTrack)); } } }

        private string _matchedRelease = string.Empty;
        public string MatchedRelease { get => _matchedRelease; set { if (_matchedRelease != value) { _matchedRelease = value; OnPropertyChanged(nameof(MatchedRelease)); } } }

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

        // New: unified action type for this proposed action
        private ProposalActionType _action = ProposalActionType.Import;
        public ProposalActionType Action { get => _action; set { if (_action != value) { _action = value; OnPropertyChanged(nameof(Action)); } } }

        // New: destination name for MoveToDestination actions
        public string DestinationName { get; set; } = string.Empty;

        // New: import status and error message
        private string _importStatus = string.Empty;
        public string ImportStatus { get => _importStatus; set { if (_importStatus != value) { _importStatus = value; OnPropertyChanged(nameof(ImportStatus)); OnPropertyChanged(nameof(IsImportFailed)); } } }

        private string _errorMessage = string.Empty;
        public string ErrorMessage { get => _errorMessage; set { if (_errorMessage != value) { _errorMessage = value; OnPropertyChanged(nameof(ErrorMessage)); OnPropertyChanged(nameof(IsImportFailed)); } } }

        public bool IsImportFailed => !string.IsNullOrWhiteSpace(_errorMessage) || string.Equals(_importStatus, "Failed", StringComparison.OrdinalIgnoreCase);

        // Retry tracking for VerifyImport actions
        public int RetryCount { get; set; }
        public int MaxRetries { get; set; }
        public DateTime? LastRetryAttempt { get; set; }
        public int? TrackFileIdToVerify { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string propName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
        }
    }
}