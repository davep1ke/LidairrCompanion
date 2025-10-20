using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using LidairrCompanion.Models;

namespace LidairrCompanion
{
    public partial class Settings : Window
    {

        public ObservableCollection<SettingItem> SettingsCollection { get; set; }

        public Settings()
        {
           

            // Create editable collection for DataGrid
            SettingsCollection = AppSettings.Current.ToCollection();
            DataContext = this;
            InitializeComponent();
        }




        protected override void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);
            // Update AppSettings from collection
            AppSettings.Current.UpdateFromCollection(SettingsCollection);
            AppSettings.Save();
        }
    }
}