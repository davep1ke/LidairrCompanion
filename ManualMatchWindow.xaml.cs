using LidairrCompanion.Helpers;
using System.Collections.Generic;
using System.Windows;

namespace LidairrCompanion
{
    public partial class ManualMatchWindow : Window
    {
        public LidarrArtist? SelectedArtist { get; private set; }

        public ManualMatchWindow(IEnumerable<LidarrArtist> candidates, string releasePath)
        {
            InitializeComponent();
            txt_Context.Text = $"Candidates for: {releasePath}";
            list_Artists.ItemsSource = candidates;
            list_Artists.SelectionChanged += (s, e) =>
            {
                btn_Ok.IsEnabled = list_Artists.SelectedItem != null;
            };
            list_Artists.MouseDoubleClick += (s, e) =>
            {
                if (list_Artists.SelectedItem is LidarrArtist a)
                {
                    SelectedArtist = a;
                    DialogResult = true;
                }
            };
        }

        private void btn_Ok_Click(object sender, RoutedEventArgs e)
        {
            if (list_Artists.SelectedItem is LidarrArtist a)
            {
                SelectedArtist = a;
                DialogResult = true;
            }
        }

        private void btn_Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}