using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LidarrCompanion.Helpers
{
    public enum ReleaseMatchType
    {
        None = 0,
        Exact = 1,
        ArtistFirst = 2,      // "Artist - Album" (folder)
        AlbumFirst = 3,       // "Album - Artist" (folder)
        ArtistFirstFile = 4,  // "Artist - Album" (single file)
        AlbumFirstFile = 5    // "Album - Artist" (single file)
    }

    public class LidarrQueueRecord : INotifyPropertyChanged
    {
        private string _title;
        private string _outputPath;
        private int _id;
        private string _downloadId;
        private ReleaseMatchType _match = ReleaseMatchType.None;
        private string _matchedArtist = string.Empty;

        public string Title
        {
            get => _title;
            set => SetProperty(ref _title, value);
        }

        public string OutputPath
        {
            get => _outputPath;
            set => SetProperty(ref _outputPath, value);
        }

        public int Id
        {
            get => _id;
            set => SetProperty(ref _id, value);
        }

        public string DownloadId
        {
            get => _downloadId;
            set => SetProperty(ref _downloadId, value);
        }

        // New: indicates the type of match found (used by the UI to colour the row)
        public ReleaseMatchType Match
        {
            get => _match;
            set => SetProperty(ref _match, value);
        }

        // New: name of the artist that this release was matched against (empty if none)
        public string MatchedArtist
        {
            get => _matchedArtist;
            set => SetProperty(ref _matchedArtist, value);
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