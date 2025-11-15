using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LidairrCompanion.Helpers
{
    // View model used to show Release + Track rows in the UI.
    public class LidarrArtistReleaseTrack : INotifyPropertyChanged
    {
        private string _release = string.Empty;
        private string _track = string.Empty;
        private bool _hasFile;
        private int _trackId;
        private int _releaseId;
        private int _albumId;
        private string _albumPath = string.Empty;
        private string _releasePath = string.Empty;
        private bool _isAssigned = false;
        private bool _isReleaseHasAssigned = false; // new
        private string _albumType = string.Empty; // new

        // Release display text (ReleaseName - Country/Format)
        public string Release
        {
            get => _release;
            set => SetProperty(ref _release, value);
        }

        // Formatted track label (e.g. "1. Track Title")
        public string Track
        {
            get => _track;
            set => SetProperty(ref _track, value);
        }

        // Whether the track already has a file on disk (pre-matched)
        public bool HasFile
        {
            get => _hasFile;
            set => SetProperty(ref _hasFile, value);
        }

        // New: Ids so we can track assignments
        public int TrackId
        {
            get => _trackId;
            set => SetProperty(ref _trackId, value);
        }

        public int ReleaseId
        {
            get => _releaseId;
            set => SetProperty(ref _releaseId, value);
        }

        // New: the album id containing this release (cached to avoid extra Lidarr queries)
        public int AlbumId
        {
            get => _albumId;
            set => SetProperty(ref _albumId, value);
        }

        // Filesystem path for the album (if provided)
        public string AlbumPath
        {
            get => _albumPath;
            set => SetProperty(ref _albumPath, value);
        }

        // Filesystem path for this specific release (if provided)
        public string ReleasePath
        {
            get => _releasePath;
            set => SetProperty(ref _releasePath, value);
        }

        // New: whether this track has been assigned (matched) in the UI
        public bool IsAssigned
        {
            get => _isAssigned;
            set => SetProperty(ref _isAssigned, value);
        }

        // New: whether another track in the same release has been assigned/matched (used to highlight sibling tracks)
        public bool IsReleaseHasAssigned
        {
            get => _isReleaseHasAssigned;
            set => SetProperty(ref _isReleaseHasAssigned, value);
        }

        // New: album type copied from album metadata (e.g., "album", "single").
        public string AlbumType
        {
            get => _albumType;
            set => SetProperty(ref _albumType, value);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
        {
            if (Equals(storage, value))
                return false;
            storage = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            return true;
        }
    }
}