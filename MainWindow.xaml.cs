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
        private List<LidarrArtist> _artists = new();

        public MainWindow()
        {
            InitializeComponent();
            AppSettings.Load();
            list_CurrentFiles.ItemsSource = _queueRecords;
            list_Files_in_Release.ItemsSource = _manualImportFiles;

            list_CurrentFiles.SelectionChanged += list_CurrentFiles_SelectionChanged;
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

        // Matches each queue record's lowest-level folder name (or filename for single-file imports)
        // against cached artists. Single-file detection uses the configured LidarrImportPath.
        // Match precedence:
        // 1) Exact folder/name == artist -> MatchType.Exact (green)
        // 2) "Artist - Album" -> ArtistFirst (LightGreen) or ArtistFirstFile (Blue)
        // 3) "Album - Artist" -> AlbumFirst (PaleGreen) or AlbumFirstFile (LightBlue)
        private void btn_autoReleaseMatchToArtist_Click(object sender, RoutedEventArgs e)
        {
            if (_artists == null || _artists.Count == 0)
            {
                MessageBox.Show("No artists loaded. Click 'Get Artists' first.", "No Artists", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var importPath = AppSettings.GetValue(SettingKey.LidarrImportPath);

            foreach (var record in _queueRecords)
            {
                record.Match = Helpers.ReleaseMatchType.None; // reset

                var lastFolder = GetLowestFolderName(record.OutputPath);
                if (string.IsNullOrWhiteSpace(lastFolder))
                    continue;

                var normalizedFolder = Normalize(lastFolder);

                bool isSingleFile = IsSingleFileRelease(record.OutputPath, importPath);

                if (isSingleFile)
                {
                    // Use filename without extension for matching
                    string fileName = Path.GetFileName(record.OutputPath) ?? lastFolder;
                    var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
                    if (string.IsNullOrWhiteSpace(nameWithoutExt))
                        continue;

                    var normalizedName = Normalize(nameWithoutExt);

                    // 1) Exact
                    var exact = _artists.FirstOrDefault(a => Normalize(a.ArtistName) == normalizedName);
                    if (exact != null)
                    {
                        record.Match = Helpers.ReleaseMatchType.Exact;
                        continue;
                    }

                    // 2) Artist - Album (left side)
                    var dashIndex = nameWithoutExt.IndexOf('-');
                    if (dashIndex > 0)
                    {
                        var left = nameWithoutExt.Substring(0, dashIndex).Trim();
                        var normalizedLeft = Normalize(left);
                        var leftMatch = _artists.FirstOrDefault(a => Normalize(a.ArtistName) == normalizedLeft);
                        if (leftMatch != null)
                        {
                            record.Match = Helpers.ReleaseMatchType.ArtistFirstFile;
                            continue;
                        }
                    }

                    // 3) Album - Artist (right side) — use last dash to allow hyphens in album names
                    var lastDash = nameWithoutExt.LastIndexOf('-');
                    if (lastDash > 0 && lastDash < nameWithoutExt.Length - 1)
                    {
                        var right = nameWithoutExt.Substring(lastDash + 1).Trim();
                        var normalizedRight = Normalize(right);
                        var rightMatch = _artists.FirstOrDefault(a => Normalize(a.ArtistName) == normalizedRight);
                        if (rightMatch != null)
                        {
                            record.Match = ReleaseMatchType.AlbumFirstFile;
                            continue;
                        }
                    }

                    // no single-file match -> remain None
                }
                else
                {
                    // Folder handling (existing logic)
                    // 1) Exact
                    var exact = _artists.FirstOrDefault(a => Normalize(a.ArtistName) == normalizedFolder);
                    if (exact != null)
                    {
                        record.Match = ReleaseMatchType.Exact;
                        continue;
                    }

                    // 2) Artist - Album (left side)
                    var dashIndex = lastFolder.IndexOf('-');
                    if (dashIndex > 0)
                    {
                        var left = lastFolder.Substring(0, dashIndex).Trim();
                        var normalizedLeft = Normalize(left);
                        var leftMatch = _artists.FirstOrDefault(a => Normalize(a.ArtistName) == normalizedLeft);
                        if (leftMatch != null)
                        {
                            record.Match = ReleaseMatchType.ArtistFirst;
                            continue;
                        }
                    }

                    // 3) Album - Artist (right side) — use last dash to allow hyphens in album names
                    var lastDash = lastFolder.LastIndexOf('-');
                    if (lastDash > 0 && lastDash < lastFolder.Length - 1)
                    {
                        var right = lastFolder.Substring(lastDash + 1).Trim();
                        var normalizedRight = Normalize(right);
                        var rightMatch = _artists.FirstOrDefault(a => Normalize(a.ArtistName) == normalizedRight);
                        if (rightMatch != null)
                        {
                            record.Match = ReleaseMatchType.AlbumFirst;
                            continue;
                        }
                    }

                    // no match -> remain None
                }
            }
        }

        // Manual match: open selection dialog with artists that contain any word from the release path.
        private void btn_manualReleaseMatchToArtist_Click(object sender, RoutedEventArgs e)
        {
            if (list_CurrentFiles.SelectedItem is not LidarrQueueRecord selectedRecord)
            {
                MessageBox.Show("Select a release from the list first.", "No selection", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (_artists == null || _artists.Count == 0)
            {
                MessageBox.Show("No artists loaded. Click 'Get Artists' first.", "No Artists", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Determine the text to split: if single file, use filename without extension; otherwise use lowest folder name.
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

            if (string.IsNullOrWhiteSpace(searchSource))
            {
                MessageBox.Show("Unable to extract words from release path.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Split into words (only keep words of length >= 2)
            var rawWords = Regex.Split(searchSource, @"\W+")
                                .Where(w => w.Length >= 2)
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToList();

            if (!rawWords.Any())
            {
                MessageBox.Show("No searchable words found in release path.", "Nothing to search", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Build candidate list: any artist whose normalized name contains any of the normalized words
            var normalizedWords = rawWords.Select(w => Normalize(w)).Where(w => !string.IsNullOrWhiteSpace(w)).ToList();
            var candidates = _artists
                .Where(a =>
                {
                    var n = Normalize(a.ArtistName);
                    return normalizedWords.Any(w => n.Contains(w, StringComparison.OrdinalIgnoreCase));
                })
                .OrderBy(a => a.ArtistName)
                .ToList();

            if (!candidates.Any())
            {
                MessageBox.Show("No candidate artists found for the selected release.", "No Results", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new ManualMatchWindow(candidates, searchSource) { Owner = this };
            var result = dlg.ShowDialog();
            if (result == true && dlg.SelectedArtist != null)
            {
                // Apply selection - mark as Exact match for now
                selectedRecord.Match = Helpers.ReleaseMatchType.Exact;

                // Optional: show which artist was selected
                MessageBox.Show($"Selected: {dlg.SelectedArtist.ArtistName} (ID: {dlg.SelectedArtist.Id})", "Artist Selected", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                // user cancelled or closed dialog
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
            }
        }

        // Made async to avoid blocking UI thread and to populate the private _artists list.
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
            var ollama = new OllamaHelper();
            string prompt = "What is the capital of France?";
            string result = await ollama.SendPromptAsync(prompt);
            MessageBox.Show(result, "Ollama Response");
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
    }
}