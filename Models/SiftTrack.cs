using System.ComponentModel;
using System.Windows.Media;

namespace LidarrCompanion.Models
{
    public class SiftTrack : INotifyPropertyChanged
    {
        private string _filePath = string.Empty;
        private string _fileName = string.Empty;
        private ImageSource? _coverArt;
        private bool _isPlaying = false;
        private string _artist = string.Empty;
        private string _title = string.Empty;
        private string _contributingArtists = string.Empty;

        public string FilePath
        {
            get => _filePath;
            set
            {
                if (_filePath != value)
                {
                    _filePath = value;
                    OnPropertyChanged(nameof(FilePath));
                }
            }
        }

        public string FileName
        {
            get => _fileName;
            set
            {
                if (_fileName != value)
                {
                    _fileName = value;
                    OnPropertyChanged(nameof(FileName));
                }
            }
        }

        public ImageSource? CoverArt
        {
            get => _coverArt;
            set
            {
                if (_coverArt != value)
                {
                    _coverArt = value;
                    OnPropertyChanged(nameof(CoverArt));
                }
            }
        }

        public bool IsPlaying
        {
            get => _isPlaying;
            set
            {
                if (_isPlaying != value)
                {
                    _isPlaying = value;
                    OnPropertyChanged(nameof(IsPlaying));
                }
            }
        }

        public string Artist
        {
            get => _artist;
            set
            {
                if (_artist != value)
                {
                    _artist = value;
                    OnPropertyChanged(nameof(Artist));
                }
            }
        }

        public string Title
        {
            get => _title;
            set
            {
                if (_title != value)
                {
                    _title = value;
                    OnPropertyChanged(nameof(Title));
                }
            }
        }

        public string ContributingArtists
        {
            get => _contributingArtists;
            set
            {
                if (_contributingArtists != value)
                {
                    _contributingArtists = value;
                    OnPropertyChanged(nameof(ContributingArtists));
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
