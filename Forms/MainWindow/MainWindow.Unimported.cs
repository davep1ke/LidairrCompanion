using LidarrCompanion.Helpers;
using LidarrCompanion.Models;
using LidarrCompanion.Services;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Threading.Tasks;
using System.Linq;

namespace LidarrCompanion
{
    public partial class MainWindow
    {
        // Collections and artist cache for the top section
        private ObservableCollection<LidarrQueueRecord> _queueRecords = new();
        private List<LidarrArtist> _artists = new();

        public void InitializeUnimportedSection()
        {
            list_CurrentFiles.ItemsSource = _queueRecords;

            // Re-wire command bindings to explicitly reference the implementations in this partial
            CommandBindings.Add(new System.Windows.Input.CommandBinding(CmdGetNextFiles, (s, e) => OnGetFilesFromLidarrClicked(s, (RoutedEventArgs)e)));
            CommandBindings.Add(new System.Windows.Input.CommandBinding(CmdGetArtists, (s, e) => OnGetArtistsClicked(s, (RoutedEventArgs)e)));
            CommandBindings.Add(new System.Windows.Input.CommandBinding(CmdAutoMatch, (s, e) => OnAutoMatchClicked(s, (RoutedEventArgs)e)));
            CommandBindings.Add(new System.Windows.Input.CommandBinding(CmdMatchArtist, (s, e) => OnManualMatchClicked(s, (RoutedEventArgs)e)));
        }

        // Newly named handlers to avoid duplicates across partials
        private async void OnGetFilesFromLidarrClicked(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;
            SetBusy("Getting files from Lidarr...");
            try
            {
                var lidarr = new LidarrHelper();
                var resultList = await lidarr.GetBlockedCompletedQueueAsync();

                _queueRecords.Clear();
                foreach (var record in resultList)
                    _queueRecords.Add(record);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Get files failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                ClearBusy();
            }
        }

        private async void OnAutoMatchClicked(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;

            if (_artists == null || _artists.Count == 0)
            {
                MessageBox.Show("No artists loaded. Click 'Get Artists' first.", "No Artists", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var importPath = AppSettings.GetValue(SettingKey.LidarrImportPath);

            SetBusy("Auto-matching releases to artists...");
            try
            {
                await Task.Run(() => MatchingService.AutoMatchReleasesToArtists(_queueRecords.ToList(), _artists, importPath));
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Auto match failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                ClearBusy();
            }
        }

        private async void list_CurrentFiles_SelectionChanged_Handler(object sender, SelectionChangedEventArgs e)
        {
            if (list_CurrentFiles.SelectedItem is LidarrQueueRecord selectedRecord)
            {
                if (_isBusy) return;

                SetBusy("Loading files for selected release...");
                try
                {
                    var lidarr = new LidarrHelper();
                    var files = await lidarr.GetFilesInReleaseAsync(selectedRecord.OutputPath);

                    _manualImportFiles.Clear();
                    foreach (var file in files)
                        _manualImportFiles.Add(file);

                    if (!string.IsNullOrWhiteSpace(selectedRecord.MatchedArtist))
                    {
                        SetBusy("Loading artist releases...");
                        try
                        {
                            await LoadArtistReleasesAsync(selectedRecord.MatchedArtist);
                        }
                        finally
                        {
                            txt_Status.Text = string.Empty;
                        }
                    }
                    else
                    {
                        _artistReleaseTracks.Clear();
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to load files: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    ClearBusy();
                }
            }
            else
            {
                _manualImportFiles.Clear();
                _artistReleaseTracks.Clear();
            }
        }

        private async void OnManualMatchClicked(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;

            if (list_CurrentFiles.SelectedItem is not LidarrQueueRecord selectedRecord)
            {
                MessageBox.Show("Select a release from the list first.", "No selection", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var importPath = AppSettings.GetValue(SettingKey.LidarrImportPath);
            bool isSingleFile = FileAndAudioService.IsSingleFileRelease(selectedRecord.OutputPath, importPath);
            string searchSource;
            if (isSingleFile)
            {
                var fileName = System.IO.Path.GetFileName(selectedRecord.OutputPath) ?? string.Empty;
                searchSource = System.IO.Path.GetFileNameWithoutExtension(fileName) ?? string.Empty;
            }
            else
            {
                searchSource = FileAndAudioService.GetLowestFolderName(selectedRecord.OutputPath) ?? string.Empty;
            }

            var dlg = new ManualMatchWindow(_artists, searchSource) { Owner = this };
            var result = dlg.ShowDialog();
            if (result == true && dlg.SelectedArtist != null)
            {
                if (_isBusy) return;

                SetBusy("Applying manual match and loading artist releases...");
                try
                {
                    var sel = dlg.SelectedArtist;
                    var already = _artists.Any(a => (a.Id != 0 && sel.Id != 0 && a.Id == sel.Id) || string.Equals(MatchingService.Normalize(a.ArtistName), MatchingService.Normalize(sel.ArtistName), StringComparison.OrdinalIgnoreCase));
                    if (!already)
                    {
                        _artists.Add(sel);
                        lbl_artistCount.Content = $"Artists: {_artists.Count}";
                    }

                    selectedRecord.Match = Helpers.ReleaseMatchType.Exact;
                    selectedRecord.MatchedArtist = dlg.SelectedArtist.ArtistName;

                    await LoadArtistReleasesAsync(selectedRecord.MatchedArtist);

                }
                finally
                {
                    ClearBusy();
                }
            }
        }

        private async void OnGetArtistsClicked(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;
            SetBusy("Getting artists from Lidarr...");
            try
            {
                var lidarr = new LidarrHelper();
                var artists = await lidarr.GetAllArtistsAsync();
                _artists = artists.ToList();

                lbl_artistCount.Content = $"Artists: {_artists.Count}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Get artists failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                ClearBusy();
            }
        }

        // Wrapper methods with original names expected by XAML - forward to implementations
        private void btn_GetFilesFromLidarr_Click(object sender, RoutedEventArgs e) => OnGetFilesFromLidarrClicked(sender, e);
        private void btn_autoReleaseMatchToArtist_Click(object sender, RoutedEventArgs e) => OnAutoMatchClicked(sender, e);
        private void list_CurrentFiles_SelectionChanged(object sender, SelectionChangedEventArgs e) => list_CurrentFiles_SelectionChanged_Handler(sender, e);
        private void btn_manualReleaseMatchToArtist_Click(object sender, RoutedEventArgs e) => OnManualMatchClicked(sender, e);
        private void btn_GetArtists_Click(object sender, RoutedEventArgs e) => OnGetArtistsClicked(sender, e);

    }
}
