using LidairrCompanion.Helpers;
using LidairrCompanion.Models;
using LidairrCompanion.Services;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace LidairrCompanion
{
    public partial class MainWindow : Window
    {
        // Commands for input bindings
        public static readonly RoutedCommand CmdGetNextFiles = new RoutedCommand();
        public static readonly RoutedCommand CmdGetArtists = new RoutedCommand();
        public static readonly RoutedCommand CmdAutoMatch = new RoutedCommand();
        public static readonly RoutedCommand CmdMatchArtist = new RoutedCommand();
        public static readonly RoutedCommand CmdMarkMatch = new RoutedCommand();
        public static readonly RoutedCommand CmdNotForImport = new RoutedCommand();
        public static readonly RoutedCommand CmdPlayTrack = new RoutedCommand();

        private ObservableCollection<LidarrQueueRecord> _queueRecords = new();
        private ObservableCollection<LidarrManualImportFile> _manualImportFiles = new();
        private ObservableCollection<LidarrArtistReleaseTrack> _artistReleaseTracks = new();
        private ObservableCollection<ProposedAction> _proposedActions = new();
        private List<LidarrArtist> _artists = new();

        // Keep quick lookup to prevent double assignment
        private HashSet<int> _assignedTrackIds = new();
        private HashSet<int> _assignedFileIds = new();

        // Busy state
        private bool _isBusy = false;

        // Audio player
        private AudioService? _audioPlayer;
        private ImportService _importService = new ImportService();
        private ProposalService _proposalService = new ProposalService();

        public MainWindow()
        {
            InitializeComponent();
            AppSettings.Load();
            list_CurrentFiles.ItemsSource = _queueRecords;
            list_Files_in_Release.ItemsSource = _manualImportFiles;
            list_Artist_Releases.ItemsSource = _artistReleaseTracks;
            list_Proposed_Actions.ItemsSource = _proposedActions;

            list_CurrentFiles.SelectionChanged += list_CurrentFiles_SelectionChanged;
            list_Files_in_Release.SelectionChanged += list_Files_in_Release_SelectionChanged;

            // Hook command bindings to existing handlers
            CommandBindings.Add(new CommandBinding(CmdGetNextFiles, (s, e) => btn_GetFilesFromLidarr_Click(btn_GetFilesFromLidarr, new RoutedEventArgs())));
            CommandBindings.Add(new CommandBinding(CmdGetArtists, (s, e) => btn_GetArtists_Click(btn_GetArtists, new RoutedEventArgs())));
            CommandBindings.Add(new CommandBinding(CmdAutoMatch, (s, e) => btn_autoReleaseMatchToArtist_Click(btn_autoReleaseMatchToArtist, new RoutedEventArgs())));
            CommandBindings.Add(new CommandBinding(CmdMatchArtist, (s, e) => btn_manualReleaseMatchToArtist_Click(btn_manualReleaseMatchToArtist, new RoutedEventArgs())));
            CommandBindings.Add(new CommandBinding(CmdMarkMatch, (s, e) => btn_MarkMatch_Click(btn_MarkMatch, new RoutedEventArgs())));
            CommandBindings.Add(new CommandBinding(CmdNotForImport, (s, e) => btn_NotForImport_Click(btn_NotForImport, new RoutedEventArgs())));
            CommandBindings.Add(new CommandBinding(CmdPlayTrack, (s, e) => btn_PlayTrack_Click(btn_PlayTrack, new RoutedEventArgs())));

            // Add KeyBindings for Alt shortcuts
            this.InputBindings.Add(new KeyBinding(CmdGetNextFiles, Key.D1, ModifierKeys.Alt));
            this.InputBindings.Add(new KeyBinding(CmdGetArtists, Key.D2, ModifierKeys.Alt));
            this.InputBindings.Add(new KeyBinding(CmdAutoMatch, Key.D3, ModifierKeys.Alt));
            this.InputBindings.Add(new KeyBinding(CmdMatchArtist, Key.A, ModifierKeys.Alt));
            this.InputBindings.Add(new KeyBinding(CmdMarkMatch, Key.M, ModifierKeys.Alt));
            this.InputBindings.Add(new KeyBinding(CmdNotForImport, Key.N, ModifierKeys.Alt));
            this.InputBindings.Add(new KeyBinding(CmdPlayTrack, Key.P, ModifierKeys.Alt));

            // Attach right-click event handler for play button
            btn_PlayTrack.MouseRightButtonUp += btn_PlayTrack_MouseRightButtonUp;
        }

        private void SetBusy(string message)
        {
            _isBusy = true;
            Dispatcher.Invoke(() =>
            {
                txt_Status.Text = message;
                // Disable main action buttons while busy
                btn_GetFilesFromLidarr.IsEnabled = false;
                btn_GetArtists.IsEnabled = false;
                btn_AI_SearchMatch.IsEnabled = false;
                btn_CheckOllama.IsEnabled = false;
                btn_Import.IsEnabled = false;
                btn_autoReleaseMatchToArtist.IsEnabled = false;
                btn_manualReleaseMatchToArtist.IsEnabled = false;
                btn_MarkMatch.IsEnabled = false;
                btn_UnselectMatch.IsEnabled = false;
                btn_Settings.IsEnabled = false;
                btn_ClearProposed.IsEnabled = false;
                btn_NotForImport.IsEnabled = false;
            });
        }

        private void ClearBusy()
        {
            _isBusy = false;
            Dispatcher.Invoke(() =>
            {
                txt_Status.Text = string.Empty;
                btn_GetFilesFromLidarr.IsEnabled = true;
                btn_GetArtists.IsEnabled = true;
                btn_AI_SearchMatch.IsEnabled = true;
                btn_CheckOllama.IsEnabled = true;
                btn_Import.IsEnabled = true;
                btn_autoReleaseMatchToArtist.IsEnabled = true;
                btn_manualReleaseMatchToArtist.IsEnabled = true;
                btn_MarkMatch.IsEnabled = true;
                btn_UnselectMatch.IsEnabled = true;
                btn_Settings.IsEnabled = true;
                btn_ClearProposed.IsEnabled = true;
                btn_NotForImport.IsEnabled = true;
            });
        }

        private void btn_Settings_Click(object sender, RoutedEventArgs e)
        {
            var settingsform = new Settings();
            settingsform.ShowDialog();
        }

        private async void btn_GetFilesFromLidarr_Click(object sender, RoutedEventArgs e)
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

        // Auto-match now delegated to MatchingService
        private async void btn_autoReleaseMatchToArtist_Click(object sender, RoutedEventArgs e)
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
                // Run CPU-bound matching off the UI thread to keep UI responsive
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

        private async void list_CurrentFiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
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

                    // If there's a matched artist, load their releases/tracks
                    if (!string.IsNullOrWhiteSpace(selectedRecord.MatchedArtist))
                    {
                        SetBusy("Loading artist releases...");
                        try
                        {
                            await LoadArtistReleasesAsync(selectedRecord.MatchedArtist);
                        }
                        finally
                        {
                            // restore message to file loading state briefly (or clear)
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

        private async void btn_manualReleaseMatchToArtist_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;

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

            var dlg = new ManualMatchWindow(_artists, searchSource) { Owner = this };
            var result = dlg.ShowDialog();
            if (result == true && dlg.SelectedArtist != null)
            {
                if (_isBusy) return; // avoid re-entrancy

                SetBusy("Applying manual match and loading artist releases...");
                try
                {
                    // If the selected artist came from an external import and isn't already in the cached list, add it
                    var sel = dlg.SelectedArtist;
                    var already = _artists.Any(a => (a.Id != 0 && sel.Id != 0 && a.Id == sel.Id) || string.Equals(MatchingService.Normalize(a.ArtistName), MatchingService.Normalize(sel.ArtistName), StringComparison.OrdinalIgnoreCase));
                    if (!already)
                    {
                        _artists.Add(sel);
                        lbl_artistCount.Content = $"Artists: {_artists.Count}";
                    }

                    // Apply selection - mark as Exact match for now
                    selectedRecord.Match = Helpers.ReleaseMatchType.Exact;
                    selectedRecord.MatchedArtist = dlg.SelectedArtist.ArtistName;

                    // Load the artist releases/tracks immediately
                    await LoadArtistReleasesAsync(selectedRecord.MatchedArtist);

                    // Optional: show which artist was selected
                    MessageBox.Show($"Selected: {dlg.SelectedArtist.ArtistName} (ID: {dlg.SelectedArtist.Id})", "Artist Selected", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                finally
                {
                    ClearBusy();
                }
            }
            else
            {
                // user cancelled or closed dialog
            }
        }

        private async void btn_GetArtists_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;
            SetBusy("Getting artists from Lidarr...");
            try
            {
                var lidarr = new LidarrHelper();
                var artists = await lidarr.GetAllArtistsAsync();
                _artists = artists.ToList(); // cache for other methods

                string artistNames = string.Join("\n", _artists.Select(a => a.ArtistName));
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

        private async void btn_AI_SearchMatch_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;

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

            SetBusy("Running AI matching...");
            try
            {
                var proposed = await MatchingService.AiMatchAsync(candidateFiles, _artistReleaseTracks, albums, tracksByReleaseId, selectedRecord.MatchedArtist, selectedRecord, _assignedFileIds, _assignedTrackIds);

                if (proposed == null || !proposed.Any())
                {
                    MessageBox.Show("No confident matches returned by the AI.", "No Matches", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // Ensure each ProposedAction has cached ArtistId/AlbumId/AlbumReleaseId using UI data
                // Delegate applying proposals to ProposalService
                _proposalService.ApplyAiProposals(proposed, _artistReleaseTracks, _manualImportFiles, _artists, _proposedActions, selectedRecord, _assignedFileIds, _assignedTrackIds);

                if (!_proposedActions.Any())
                {
                    MessageBox.Show("No confident matches could be applied (all matches were skipped or duplicates).", "No Applied Matches", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"AI match failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                ClearBusy();
            }
        }

        private async void btn_CheckOllama_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;
            SetBusy("Checking Ollama...");
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
            finally
            {
                ClearBusy();
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

            // compute path fallback: prefer release path, then album path, then artist path + release (fallback to empty)
            var artistPath = _artists.FirstOrDefault(a => MatchingService.Normalize(a.ArtistName) == MatchingService.Normalize(selectedQueueRecord?.MatchedArtist ?? string.Empty))?.Path ?? string.Empty;
            var fallbackPath = string.Empty;
            if (!string.IsNullOrWhiteSpace(artistPath) && !string.IsNullOrWhiteSpace(selectedTrack.Release))
            {
                // join artist path and release name
                try { fallbackPath = System.IO.Path.Combine(artistPath, selectedTrack.Release); } catch { fallbackPath = artistPath; }
            }
            else if (!string.IsNullOrWhiteSpace(artistPath))
            {
                fallbackPath = artistPath;
            }

            // Remove any existing proposal for this file (e.g. a previous move) before adding the assignment
            var existingForSelected = _proposedActions.FirstOrDefault(p => p.FileId == selectedFile.Id);
            if (existingForSelected != null) _proposedActions.Remove(existingForSelected);

            // Use ProposalService to create and add proposals
            _proposalService.CreateManualAssignment(selectedFile, selectedTrack, selectedQueueRecord, _artists, _proposedActions, _manualImportFiles, _assignedFileIds, _assignedTrackIds);
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
                // clear any NotSelected mark on file when unselecting its proposal
                file.IsMarkedNotSelected = false;
                _assignedFileIds.Remove(file.Id);
            }

            if (track != null)
            {
                track.IsAssigned = false;
                _assignedTrackIds.Remove(track.TrackId);
            }

            _proposedActions.Remove(toRemove);

            // capture release context before removing
            var releaseKey = toRemove.OriginalRelease ?? string.Empty;

            // If there are no remaining assignment proposals for this release, remove any move-to-not-selected proposals for the same release
            var hasAssignmentsForRelease = _proposedActions.Any(p => !p.IsMoveToNotSelected && (p.OriginalRelease ?? string.Empty) == releaseKey);
            if (!hasAssignmentsForRelease)
            {
                var movesToRemove = _proposedActions.Where(p => p.IsMoveToNotSelected && (p.OriginalRelease ?? string.Empty) == releaseKey).ToList();
                foreach (var m in movesToRemove)
                {
                    // clear corresponding file highlight
                    var f = _manualImportFiles.FirstOrDefault(x => x.Id == m.FileId);
                    if (f != null) f.IsMarkedNotSelected = false;

                    _proposedActions.Remove(m);
                }
            }
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
                                AlbumId = album.Id,
                                AlbumPath = album.Path ?? string.Empty,
                                ReleasePath = release.Path ?? string.Empty,
                                IsAssigned = _assignedTrackIds.Contains(track.Id),
                                AlbumType = album.AlbumType ?? string.Empty
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

                    var selFileName = selectedFile.Name;
                    var selFileId = selectedFile.Id;

                    // read boost from settings (default10)
                    int releaseBoost = AppSettings.Current.GetTyped<int>(SettingKey.ReleaseBoost);
                    if (releaseBoost <= 0) releaseBoost = 10;

                    // Determine whether there are other assigned files/tracks in a release
                    // We'll boost a release's tracks by configured boost if there are other assignments in that release
                    items = items.OrderByDescending(i =>
                    {
                        double baseScore = MatchingService.ComputeMatchScore(selFileName, i);

                        // Do not apply release-level boost if the track already has a file or the selected file is already assigned
                        if (i.HasFile || selectedFile.IsAssigned)
                            return baseScore;

                        // If the selected file is already part of a proposed action for this release, do not boost
                        var selectedFileAlreadyMappedToThisRelease = _proposedActions.Any(a => a.FileId == selFileId && a.AlbumReleaseId == i.ReleaseId);
                        if (selectedFileAlreadyMappedToThisRelease)
                            return baseScore;

                        // Check if any other track in the same release is assigned (either via IsAssigned on track rows or via assigned ID set or proposed actions with different file)
                        bool otherAssignedInRelease = _artistReleaseTracks.Any(t => t.ReleaseId == i.ReleaseId && t.IsAssigned && t.TrackId != i.TrackId)
                            || _assignedTrackIds.Any(id => _artistReleaseTracks.Any(t => t.ReleaseId == i.ReleaseId && t.TrackId == id && t.TrackId != i.TrackId))
                            || _proposedActions.Any(a => a.AlbumReleaseId == i.ReleaseId && a.FileId != selFileId);

                        if (otherAssignedInRelease)
                            return baseScore + releaseBoost;

                        return baseScore;
                    });
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

            // Ensure list scrolls to top after re-ordering (so best matches are immediately visible)
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_artistReleaseTracks.Count > 0)
                    list_Artist_Releases.ScrollIntoView(_artistReleaseTracks[0]);
            }), System.Windows.Threading.DispatcherPriority.Background);

            // Update sibling-release highlighting after any reorder/populate
            UpdateReleaseAssignmentHighlights();
        }

        // Update IsReleaseHasAssigned on artist release rows so the UI can highlight
        // other tracks in a release when some tracks are matched/assigned.
        private void UpdateReleaseAssignmentHighlights()
        {
            try
            {
                // Determine which releases have any assigned tracks or proposed actions (non-move)
                var releasesWithAssigned = new HashSet<int>(_artistReleaseTracks.Where(t => t.IsAssigned).Select(t => t.ReleaseId));

                // Include releases that have proposed actions targeting that release (and not move-to-not-selected / not-for-import)
                foreach (var pa in _proposedActions)
                {
                    if (!pa.IsMoveToNotSelected && !pa.IsNotForImport && pa.AlbumReleaseId != 0)
                        releasesWithAssigned.Add(pa.AlbumReleaseId);
                }

                // Now set the flag on each track: true when the release has assigned tracks but this track itself is not assigned
                foreach (var tr in _artistReleaseTracks)
                {
                    tr.IsReleaseHasAssigned = releasesWithAssigned.Contains(tr.ReleaseId) && !tr.IsAssigned;
                }
            }
            catch
            {
                // ignore
            }
        }

        /// <summary>
        /// Actually perform the import of proposed actions into Lidarr.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private async void btn_Import_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;

            if (_proposedActions == null || !_proposedActions.Any())
            {
                MessageBox.Show("No proposed actions to import.", "Nothing to import", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            SetBusy("Importing files to Lidarr...");
            try
            {
                // Snapshot to avoid concurrent modification
                var actionsSnapshot = new List<ProposedAction>(_proposedActions);

                var result = await _importService.ImportAsync(actionsSnapshot, _manualImportFiles, _proposedActions, _artistReleaseTracks, _assignedFileIds, _assignedTrackIds);

                if (result == null)
                {
                    MessageBox.Show("Import failed: unknown error.", "Import Results", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                else if (result.FailCount >0)
                {
                    MessageBox.Show($"Import completed with failures. Success: {result.SuccessCount}, Failed: {result.FailCount}", "Import Results", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                else
                {
                    MessageBox.Show($"Import completed. Success: {result.SuccessCount}", "Import Results", MessageBoxButton.OK, MessageBoxImage.Information);
                }

                // Ensure UI state is consistent (ImportService already clears assigned sets and track flags but refresh UI as well)
                _assignedFileIds.Clear();
                _assignedTrackIds.Clear();
                foreach (var track in _artistReleaseTracks)
                    track.IsAssigned = false;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Import failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                ClearBusy();
            }
        }

        private void btn_ClearProposed_Click(object sender, RoutedEventArgs e)
        {
            // clear any file-level NotSelected marks before clearing proposals
            foreach (var f in _manualImportFiles)
                f.IsMarkedNotSelected = false;

            _proposedActions.Clear();
        }

        private void btn_NotForImport_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var pa = list_Proposed_Actions.SelectedItem as ProposedAction;
                var selFile = list_Files_in_Release.SelectedItem as LidarrManualImportFile;
                var currentQueueRecord = list_CurrentFiles.SelectedItem as LidarrQueueRecord;

                _proposalService.MarkNotForImport(pa, selFile, _proposedActions, _manualImportFiles, _queueRecords, currentQueueRecord);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Operation failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void btn_PlayTrack_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (list_Files_in_Release.SelectedItem is not LidarrManualImportFile selectedFile)
                {
                    MessageBox.Show("Select a file from 'Unimported Release Files' first.", "No file selected", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var serverImportPath = AppSettings.GetValue(SettingKey.LidarrImportPath);
                var localMapping = AppSettings.GetValue(SettingKey.LidarrImportPathLocal);

                // Toggle play/stop
                if (_audioPlayer != null && _audioPlayer.IsPlaying)
                {
                    _audioPlayer.Stop();
                    _audioPlayer.Dispose();
                    _audioPlayer = null;
                    btn_PlayTrack.Content = "Play Track";
                    return;
                }

                _audioPlayer = new AudioService();
                try
                {
                    _audioPlayer.PlayMapped(selectedFile.Path ?? string.Empty, serverImportPath, localMapping);
                    btn_PlayTrack.Content = "Stop";
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to play file: {ex.Message}", "Playback error", MessageBoxButton.OK, MessageBoxImage.Error);
                    _audioPlayer.Dispose();
                    _audioPlayer = null;
                    btn_PlayTrack.Content = "Play Track";
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Playback error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Right-click handler to advance audio by30 seconds
        private void btn_PlayTrack_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (_audioPlayer != null && _audioPlayer.IsPlaying)
                {
                    _audioPlayer.AdvanceBy(TimeSpan.FromSeconds(30));
                }
            }
            catch
            {
                // ignore
            }
        }

        private void btn_OpenReleaseFolder_Click(object sender, RoutedEventArgs e)
        {
            if (list_Files_in_Release.SelectedItem is not LidarrManualImportFile selectedFile)
            {
                MessageBox.Show("Select a file from 'Unimported Release Files' first.", "No file selected", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var serverImportPath = AppSettings.GetValue(SettingKey.LidarrImportPath);
            var localMapping = AppSettings.GetValue(SettingKey.LidarrImportPathLocal);
            try
            {
                var folder = AudioService.OpenContainingFolder(selectedFile.Path ?? string.Empty, serverImportPath, localMapping);
                // optional: display status
                txt_Status.Text = folder;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to open folder: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void list_Files_in_Release_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            try
            {
                if (cmb_SortMode.SelectedItem is ComboBoxItem item)
                {
                    var mode = item.Content?.ToString() ?? string.Empty;
                    ApplyArtistReleasesSort(mode);
                }
                else
                {
                    ApplyArtistReleasesSort(string.Empty);
                }
            }
            catch
            {
                // ignore
            }
        }

        
    }
}