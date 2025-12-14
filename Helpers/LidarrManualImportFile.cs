using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LidarrCompanion.Helpers
{
    public class LidarrManualImportFile : INotifyPropertyChanged
    {
        private string _path = string.Empty;
        private string _name = string.Empty;
        private int _id;
        private ProposalActionType? _proposedActionType;

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

        // New: which action (if any) is associated with this file for UI highlighting
        public ProposalActionType? ProposedActionType
        {
            get => _proposedActionType;
            set => SetProperty(ref _proposedActionType, value);
        }

        // Quality information returned by Lidarr for this file (quality + revision)
        public LidarrManualFileQuality? Quality { get; set; }

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