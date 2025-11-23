using System.ComponentModel;

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

        // New: indicate this proposed action is a move to NotSelectedPath rather than import
        private bool _isMoveToNotSelected;
        public bool IsMoveToNotSelected { get => _isMoveToNotSelected; set { if (_isMoveToNotSelected != value) { _isMoveToNotSelected = value; OnPropertyChanged(nameof(IsMoveToNotSelected)); } } }
        private string _moveDestinationPath = string.Empty;
        public string MoveDestinationPath { get => _moveDestinationPath; set { if (_moveDestinationPath != value) { _moveDestinationPath = value; OnPropertyChanged(nameof(MoveDestinationPath)); } } }

        // New: explicitly mark a file as NotForImport (user requested). Different UI highlight.
        private bool _isNotForImport;
        public bool IsNotForImport { get => _isNotForImport; set { if (_isNotForImport != value) { _isNotForImport = value; OnPropertyChanged(nameof(IsNotForImport)); } } }

        // New: human-readable action type for UI (e.g. "Import" or "Move to Not Import")
        private string _actionType = "Import";
        public string ActionType { get => _actionType; set { if (_actionType != value) { _actionType = value; OnPropertyChanged(nameof(ActionType)); } } }

        // New: import status and error message
        private string _importStatus = string.Empty;
        public string ImportStatus { get => _importStatus; set { if (_importStatus != value) { _importStatus = value; OnPropertyChanged(nameof(ImportStatus)); OnPropertyChanged(nameof(IsImportFailed)); } } }

        private string _errorMessage = string.Empty;
        public string ErrorMessage { get => _errorMessage; set { if (_errorMessage != value) { _errorMessage = value; OnPropertyChanged(nameof(ErrorMessage)); OnPropertyChanged(nameof(IsImportFailed)); } } }

        public bool IsImportFailed => !string.IsNullOrWhiteSpace(_errorMessage) || string.Equals(_importStatus, "Failed", StringComparison.OrdinalIgnoreCase);

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string propName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
        }
    }
}