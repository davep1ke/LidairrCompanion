using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LidairrCompanion.Helpers
{
    public class LidarrManualImportFile : INotifyPropertyChanged
    {
        private string _path = string.Empty;
        private string _name = string.Empty;
        private int _id;
        private bool _isAssigned;

        public string Path
        {
            get => _path;
            set => SetProperty(ref _path, value);
        }

        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        public int Id
        {
            get => _id;
            set => SetProperty(ref _id, value);
        }

        // New: whether this file has already been assigned/matched in the UI
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