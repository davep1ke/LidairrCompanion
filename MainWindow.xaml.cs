using LidairrCompanion.Models;
using LidairrCompanion.Helpers;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.IO;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Threading.Tasks;
using System;

namespace LidairrCompanion
{
    public partial class MainWindow : Window
    {
        private ObservableCollection<LidarrQueueRecord> _queueRecords = new();
        private ObservableCollection<LidarrManualImportFile> _manualImportFiles = new();
        private ObservableCollection<LidarrArtistReleaseTrack> _artistReleaseTracks = new();
        private ObservableCollection<ProposedAction> _proposedActions = new();
        private List<LidarrArtist> _artists = new();

        // Keep quick lookup to prevent double assignment
        private HashSet<int> _assignedTrackIds = new();
        private HashSet<int> _assignedFileIds = new();

        public MainWindow()
        {
            InitializeComponent();
            AppSettings.Load();
            list_CurrentFiles.ItemsSource = _queueRecords;
            list_Files_in_Release.ItemsSource = _manualImportFiles;
            list_Artist_Releases.ItemsSource = _artistReleaseTracks;
            list_Proposed_Actions.ItemsSource = _proposedActions;

            list_CurrentFiles.SelectionChanged += list_CurrentFiles_SelectionChanged;
            // React when the selected file changes so 'by Best' sorting can update
            list_Files_in_Release.SelectionChanged += list_Files_in_Release_SelectionChanged;
        }

        private void btn_Settings_Click(object sender, RoutedEventArgs e)
        {
            var settingsform = new Settings();
            settingsform.ShowDialog();
        }

        private async void btn_GetFilesFromLidarr_Click(object sender, RoutedEventArgs e)
        {
            var lidarr = new LidarrHelper();
            var resultList = await lidarr.GetBlockedCompletedQueueAsync();

            _queueRecords.Clear();
            foreach (var record in resultList)
                _queueRecords.Add(record);
        }

        // Auto-match now delegated to MatchingService
        private void btn_autoReleaseMatchToArtist_Click(object sender, RoutedEventArgs e)
        {
            if (_artists == null || _artists.Count == 0)
            {
                MessageBox.Show("No artists loaded. Click 'Get Artists' first.", "No Artists", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var importPath = AppSettings.GetValue(SettingKey.LidarrImportPath);

            // Delegate the matching logic to the service which will update the queue records in-place
            try
            {
                MatchingService.AutoMatchReleasesToArtists(_queueRecords.ToList(), _artists, importPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Auto match failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void list_CurrentFiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (list_CurrentFiles.SelectedItem is LidarrQueueRecord selectedRecord)
            {
                var lidarr = new LidarrHelper();
                var files = await lidarr.GetFilesInReleaseAsync(selectedRecord.OutputPath);

                _manualImportFiles.Clear();
                foreach (var file in files)
                    _manualImportFiles.Add(file);

                // If there's a matched artist, load their releases/tracks
                if (!string.IsNullOrWhiteSpace(selectedRecord.MatchedArtist))
                {
                    await LoadArtistReleasesAsync(selectedRecord.MatchedArtist);
                }
                else
                {
                    _artistReleaseTracks.Clear();
                }
            }
            else
            {
                _manualImportFiles.Clear();
                _artistReleaseTracks.Clear();
            }
        }

        private async void btn_manualReleaseMatchToArtist_Click(object sender, RoutedEventArgs e)
        {
            if (list_CurrentFiles.SelectedItem is not LidarrQueueRecord selectedRecord)
            {
                MessageBox.Show("Select a release from the list first.", "No selection", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Determine context text to show in the dialog (filename or lowest folder)
            var importPath = AppSettings.GetValue(SettingKey.LidarrImportPath);
            bool isSingleFile = IsSingleFileRelease(selectedRecord.OutputPath, importPath);
            string searchSource;
            if (isSingleFile)
            {
                var fileName = Path.GetFileName(selectedRecord.OutputPath) ?? string.Empty;
                searchSource = Path.GetFileNameWithoutExtension(fileName) ?? string.Empty;
            }
            else
            {
                searchSource = GetLowestFolderName(selectedRecord.OutputPath) ?? string.Empty;
            }

            // Open the manual match dialog with the cached artists (may be empty). The dialog supports external search.
            var candidates = _artists ?? new List<LidarrArtist>();
            var dlg = new ManualMatchWindow(candidates, searchSource) { Owner = this };
            var result = dlg.ShowDialog();
            if (result == true && dlg.SelectedArtist != null)
            {
                // Apply selection - mark as Exact match for now
                selectedRecord.Match = Helpers.ReleaseMatchType.Exact;
                selectedRecord.MatchedArtist = dlg.SelectedArtist.ArtistName;

                // Load the artist releases/tracks immediately
                await LoadArtistReleasesAsync(selectedRecord.MatchedArtist);

                // Optional: show which artist was selected
                MessageBox.Show($"Selected: {dlg.SelectedArtist.ArtistName} (ID: {dlg.SelectedArtist.Id})", "Artist Selected", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                // user cancelled or closed dialog
            }
        }

        private async void btn_GetArtists_Click(object sender, RoutedEventArgs e)
        {
            var lidarr = new LidarrHelper();
            var artists = await lidarr.GetAllArtistsAsync();
            _artists = artists.ToList(); // cache for other methods

            string artistNames = string.Join("\n", _artists.Select(a => a.ArtistName));
            lbl_artistCount.Content = $"Artists: {_artists.Count}";
        }

        private async void btn_AI_SearchMatch_Click(object sender, RoutedEventArgs e)
        {
            // Use the lists behind the UI instead of rebuilding from Lidarr
            if (list_CurrentFiles.SelectedItem is not LidarrQueueRecord selectedRecord)
            {
                MessageBox.Show("Select a release from the list first.", "No selection", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (string.IsNullOrWhiteSpace(selectedRecord.MatchedArtist))
            {
                MessageBox.Show("Selected release has no matched artist. Use Auto or Manual match first.", "No Artist", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Candidate files are the ones currently shown in the UI list (and not already assigned)
            var candidateFiles = _manualImportFiles.Where(f => !f.IsAssigned && !_assignedFileIds.Contains(f.Id)).ToList();
            if (!candidateFiles.Any())
            {
                MessageBox.Show("No unassigned files available to match.", "Nothing to match", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Build albums/tracks structures from the UI using the MatchingService helper
            var (albums, tracksByReleaseId) = MatchingService.BuildAlbumsFromUiCollections(_manualImportFiles, _artistReleaseTracks, selectedRecord.MatchedArtist);

            try
            {
                var proposed = await MatchingService.AiMatchAsync(candidateFiles, _artistReleaseTracks, albums, tracksByReleaseId, selectedRecord.MatchedArtist, selectedRecord, _assignedFileIds, _assignedTrackIds);

                if (proposed == null || !proposed.Any())
                {
                    MessageBox.Show("No confident matches returned by the AI.", "No Matches", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                foreach (var p in proposed)
                {
                    _proposedActions.Add(p);
                }

                if (!_proposedActions.Any())
                {
                    MessageBox.Show("No confident matches could be applied (all matches were skipped or duplicates).", "No Applied Matches", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"AI match failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void btn_CheckOllama_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var ollama = new OllamaHelper();
                // Simple health/warmup prompt
                var prompt = "Health check: respond with only the word OK.";
                Console.WriteLine("Check Ollama prompt:");
                Console.WriteLine(prompt);

                var response = await ollama.SendPromptAsync(prompt); // uses streaming and writes chunks to console
                Console.WriteLine(); // newline after streaming
                var shortResp = string.IsNullOrWhiteSpace(response) ? "(no response)" : response.Trim();
                MessageBox.Show($"Ollama response: {shortResp}", "Ollama Check", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ollama check failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Get the last folder segment from a path. If the path is a file, return its parent folder name.
        private static string? GetLowestFolderName(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            // Trim trailing separators
            path = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // If path looks like a file (has extension), return the parent folder's name
            if (Path.HasExtension(path))
            {
                var parent = Path.GetDirectoryName(path);
                if (string.IsNullOrWhiteSpace(parent))
                    return Path.GetFileName(path); // fallback
                return Path.GetFileName(parent);
            }

            // Otherwise return the last segment
            return Path.GetFileName(path);
        }

        // Normalise strings for comparison: lower-case, remove punctuation, collapse whitespace
        private static string Normalize(string s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return string.Empty;

            var lowered = s.ToLowerInvariant();
            lowered = Regex.Replace(lowered, @"[.,;:!""'()\[\]\/\\\-_]+", " ");
            lowered = Regex.Replace(lowered, @"\s+", " ").Trim();
            return lowered;
        }

        // Normalize path for simple equality checks: unify separators and trim trailing separators.
        private static string NormalizePathForComparison(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            var p = path.Trim();
            p = p.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            p = p.TrimEnd(Path.DirectorySeparatorChar);
            return p;
        }

        // Heuristic: determine whether this release should be treated as a single file.
        // Rule:
        // - If configured importPath is empty -> false
        // - If outputPath equals importPath (after normalizing) -> single file
        // - If outputPath is a file and its parent directory equals importPath -> single file
        private static bool IsSingleFileRelease(string? outputPath, string? importPath)
        {
            if (string.IsNullOrWhiteSpace(importPath) || string.IsNullOrWhiteSpace(outputPath))
                return false;

            try
            {
                var normImport = NormalizePathForComparison(importPath);
                var normOutput = NormalizePathForComparison(outputPath);

                if (string.Equals(normOutput, normImport, StringComparison.OrdinalIgnoreCase))
                    return true;

                if (Path.HasExtension(outputPath))
                {
                    var parent = Path.GetDirectoryName(outputPath);
                    if (!string.IsNullOrWhiteSpace(parent))
                    {
                        var normParent = NormalizePathForComparison(parent);
                        if (string.Equals(normParent, normImport, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
            }
            catch
            {
                // If any path parsing fails, fall back to treating as folder (safer).
            }

            return false;
        }

        private async void btn_MarkMatch_Click(object sender, RoutedEventArgs e)
        {
            // Need one selected file and one selected track
            if (list_Files_in_Release.SelectedItem is not LidarrManualImportFile selectedFile)
            {
                MessageBox.Show("Select a file from 'Unimported Release Files' first.", "No file selected", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (list_Artist_Releases.SelectedItem is not LidarrArtistReleaseTrack selectedTrack)
            {
                MessageBox.Show("Select a track from 'Artist Release Files' first.", "No track selected", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Prevent re-assignment
            if (selectedFile.IsAssigned || _assignedFileIds.Contains(selectedFile.Id))
            {
                MessageBox.Show("This file has already been assigned.", "Already assigned", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (selectedTrack.IsAssigned || _assignedTrackIds.Contains(selectedTrack.TrackId))
            {
                MessageBox.Show("This track has already been assigned.", "Already assigned", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // If track already has a file on disk, confirm overwrite
            if (selectedTrack.HasFile)
            {
                var confirmOverwrite = MessageBox.Show("Selected track already has a file. Assigning will overwrite it. Continue?", "Overwrite Confirmation", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (confirmOverwrite != MessageBoxResult.Yes)
                    return;
            }

            // Mark assigned
            selectedFile.IsAssigned = true;
            selectedTrack.IsAssigned = true;
            _assignedFileIds.Add(selectedFile.Id);
            _assignedTrackIds.Add(selectedTrack.TrackId);

            // Highlight both lists by selecting the items (ListView selection visual)
            list_Files_in_Release.SelectedItem = selectedFile;
            list_Artist_Releases.SelectedItem = selectedTrack;

            // Build proposed action (originalRelease: try selected queue record title if available)
            var selectedQueueRecord = list_CurrentFiles.SelectedItem as LidarrQueueRecord;
            var originalRelease = selectedQueueRecord?.Title ?? Path.GetFileName(selectedFile.Path) ?? string.Empty;

            _proposedActions.Add(new ProposedAction
            {
                OriginalFileName = selectedFile.Name,
                OriginalRelease = originalRelease,
                MatchedArtist = selectedQueueRecord?.MatchedArtist ?? string.Empty,
                MatchedTrack = selectedTrack.Track,
                MatchedRelease = selectedTrack.Release,
                TrackId = selectedTrack.TrackId,
                FileId = selectedFile.Id
            });
        }

        private void btn_UnselectMatch_Click(object sender, RoutedEventArgs e)
        {
            if (list_Proposed_Actions.SelectedItem is not ProposedAction toRemove)
            {
                MessageBox.Show("Select a proposed action to unselect.", "No selection", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Find corresponding file and track items and clear assignments
            var file = _manualImportFiles.FirstOrDefault(f => f.Id == toRemove.FileId);
            var track = _artistReleaseTracks.FirstOrDefault(t => t.TrackId == toRemove.TrackId && t.Release == toRemove.MatchedRelease);

            if (file != null)
            {
                file.IsAssigned = false;
                _assignedFileIds.Remove(file.Id);
            }

            if (track != null)
            {
                track.IsAssigned = false;
                _assignedTrackIds.Remove(track.TrackId);
            }

            _proposedActions.Remove(toRemove);
        }

        private async Task LoadArtistReleasesAsync(string artistName)
        {
            if (_artists == null || _artists.Count == 0)
                return;

            // Find the artist id (normalize for robustness)
            var artist = _artists.FirstOrDefault(a => MatchingService.Normalize(a.ArtistName) == MatchingService.Normalize(artistName));
            if (artist == null)
                return;

            _artistReleaseTracks.Clear();

            try
            {
                var albums = await MatchingService.GetAlbumsForArtistAsync(artist.Id);


                // Re-run the original population logic using LidarrHelper directly for tracks
                var lidarr = new LidarrHelper();
                foreach (var album in albums)
                {
                    foreach (var release in album.Releases)
                    {
                        var tracks = await lidarr.GetTracksByReleaseAsync(release.Id);

                        var format = string.IsNullOrWhiteSpace(release.Format) ? string.Empty : release.Format;
                        var country = string.IsNullOrWhiteSpace(release.Country) ? string.Empty : release.Country;

                        var releaseDisplay = release.GetDisplayText();

                        foreach (var track in tracks)
                        {
                            var trackLabel = string.IsNullOrWhiteSpace(track.TrackNumber) ? track.Title : $"{track.TrackNumber}. {track.Title}";

                            var tr = new LidarrArtistReleaseTrack
                            {
                                Release = releaseDisplay,
                                Track = trackLabel,
                                HasFile = track.HasFile,
                                TrackId = track.Id,
                                ReleaseId = release.Id,
                                IsAssigned = _assignedTrackIds.Contains(track.Id)
                            };

                            _artistReleaseTracks.Add(tr);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading artist releases: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void cmb_SortMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cmb_SortMode.SelectedItem is ComboBoxItem item)
            {
                var mode = item.Content?.ToString() ?? string.Empty;
                ApplyArtistReleasesSort(mode);
            }
        }

        private void ApplyArtistReleasesSort(string mode)
        {
            IEnumerable<LidarrArtistReleaseTrack> items = _artistReleaseTracks.ToList();

            switch (mode)
            {
                case "by Release":
                    items = items.OrderBy(i => i.Release).ThenBy(i => i.Track);
                    break;
                case "by Track Num":
                    items = items.OrderBy(i => MatchingService.ParseTrackNumber(i.Track)).ThenBy(i => i.Track);
                    break;
                case "by Track Name":
                    items = items.OrderBy(i => MatchingService.StripTrackNumberPrefix(i.Track)).ThenBy(i => i.Track);
                    break;
                case "by Best":
                    // If nothing selected in files list, fallback to release sort
                    if (!(list_Files_in_Release.SelectedItem is LidarrManualImportFile selectedFile))
                    {
                        items = items.OrderBy(i => i.Release).ThenBy(i => i.Track);
                        break;
                    }
                    var fileName = selectedFile.Name;
                    items = items.OrderByDescending(i => MatchingService.ComputeMatchScore(fileName, i));
                    break;
                default:
                    items = items.OrderBy(i => i.Release).ThenBy(i => i.Track);
                    break;
            }

            // Re-populate the observable collection preserving selection
            var selected = list_Artist_Releases.SelectedItem as LidarrArtistReleaseTrack;
            _artistReleaseTracks.Clear();
            foreach (var it in items)
                _artistReleaseTracks.Add(it);

            if (selected != null)
            {
                var toSelect = _artistReleaseTracks.FirstOrDefault(t => t.TrackId == selected.TrackId && t.ReleaseId == selected.ReleaseId);
                if (toSelect != null) list_Artist_Releases.SelectedItem = toSelect;
            }
        }

        // New: when selected file changes, if sort mode is 'by Best' re-apply sorting
        private void list_Files_in_Release_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                if (cmb_SortMode.SelectedItem is ComboBoxItem item)
                {
                    var mode = item.Content?.ToString() ?? string.Empty;
                    if (mode == "by Best")
                    {
                        ApplyArtistReleasesSort(mode);
                    }
                }
            }
            catch
            {
                // Do not let UI selection change crash the app
            }
        }
    }
}