using System.ComponentModel;
using System.Text.Json.Serialization;

namespace LidarrCompanion.Models
{
    public class ImportDestination : INotifyPropertyChanged
    {
        private string _name = string.Empty;
        private string _destinationPath = string.Empty;
        private bool _backupFiles = false;
        private bool _copyFiles = false;
        private bool _requireArtwork = false;
        private string _color = "#87CEEB"; // Default: Sky Blue
        private string _colorDark = "#4682B4"; // Default: Steel Blue (darker)

        [JsonInclude]
        public string Name 
        { 
            get => _name; 
            set { if (_name != value) { _name = value; OnPropertyChanged(nameof(Name)); } } 
        }

        [JsonInclude]
        public string DestinationPath 
        { 
            get => _destinationPath; 
            set { if (_destinationPath != value) { _destinationPath = value; OnPropertyChanged(nameof(DestinationPath)); } } 
        }

        [JsonInclude]
        public bool BackupFiles 
        { 
            get => _backupFiles; 
            set { if (_backupFiles != value) { _backupFiles = value; OnPropertyChanged(nameof(BackupFiles)); } } 
        }

        [JsonInclude]
        public bool CopyFiles 
        { 
            get => _copyFiles; 
            set { if (_copyFiles != value) { _copyFiles = value; OnPropertyChanged(nameof(CopyFiles)); } } 
        }

        [JsonInclude]
        public bool RequireArtwork 
        { 
            get => _requireArtwork; 
            set { if (_requireArtwork != value) { _requireArtwork = value; OnPropertyChanged(nameof(RequireArtwork)); } } 
        }

        [JsonInclude]
        public string Color 
        { 
            get => _color; 
            set { if (_color != value) { _color = value; OnPropertyChanged(nameof(Color)); } } 
        }

        [JsonInclude]
        public string ColorDark 
        { 
            get => _colorDark; 
            set { if (_colorDark != value) { _colorDark = value; OnPropertyChanged(nameof(ColorDark)); } } 
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        
        protected void OnPropertyChanged(string propName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
        }
    }
}
