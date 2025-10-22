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
        private bool _isAssigned = false;

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

        // New: whether this track has been assigned (matched) in the UI
        public bool IsAssigned
        {
            get => _isAssigned;
            set => SetProperty(ref _isAssigned, value);
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