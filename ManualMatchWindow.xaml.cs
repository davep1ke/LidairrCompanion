using LidairrCompanion.Helpers;
using System.Collections.Generic;
using System.Windows;
using System.Linq;
using System.Collections.ObjectModel;
using System.Text.Json;

namespace LidairrCompanion
{
    public partial class ManualMatchWindow : Window
    {
        private readonly ObservableCollection<ManualMatchItem> _externalItems = new();
        private readonly IList<LidarrArtist> _internalCandidates;
        private readonly string _originalContext;

        public LidarrArtist? SelectedArtist { get; private set; }

        public ManualMatchWindow(IEnumerable<LidarrArtist> candidates, string releasePath)
        {
            InitializeComponent();
            _originalContext = string.IsNullOrWhiteSpace(releasePath) ? "Internal Artists" : $"{releasePath} (Internal Artists)";
            txt_Context.Text = _originalContext;

            _internalCandidates = candidates.ToList();

            // Initially show internal items
            list_Artists.ItemsSource = _internalCandidates;

            list_Artists.SelectionChanged += (s, e) => btn_Ok.IsEnabled = list_Artists.SelectedItem != null;
            list_Artists.MouseDoubleClick += (s, e) =>
            {
                if (list_Artists.SelectedItem is ManualMatchItem mi && mi.Artist != null && !mi.IsExternal)
                {
                    SelectedArtist = mi.Artist;
                    DialogResult = true;
                }
            };

            txt_Search.TextChanged += Txt_Search_TextChanged;

            // Pre-fill search box with text before the first hyphen (or full string if none)
            var initialTerm = ExtractInitialSearchTerm(releasePath);
            if (!string.IsNullOrWhiteSpace(initialTerm))
            {
                txt_Search.Text = initialTerm;
            }
        }

        private static string ExtractInitialSearchTerm(string? releasePath)
        {
            if (string.IsNullOrWhiteSpace(releasePath))
                return string.Empty;

            var idx = releasePath.IndexOf('-');
            if (idx >0)
                return releasePath.Substring(0, idx).Trim();

            return releasePath.Trim();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Autofocus the search box when dialog opens
            txt_Search.Focus();
            txt_Search.SelectAll();
        }

        private void Txt_Search_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            var term = txt_Search.Text?.Trim() ?? string.Empty;
            // Filter the currently visible set (internal by default, external after search)
            if (list_Artists.ItemsSource == _externalItems || (_externalItems.Count >0 && !string.IsNullOrWhiteSpace(term)))
            {
                var filtered = _externalItems.Where(i => i.DisplayName.Contains(term, System.StringComparison.OrdinalIgnoreCase)).ToList();
                list_Artists.ItemsSource = filtered;
            }
            else
            {
                var filtered = _internalCandidates.Where(i => i.ArtistName.Contains(term, System.StringComparison.OrdinalIgnoreCase)).ToList();
                list_Artists.ItemsSource = filtered;
            }
        }

        private async void btn_SearchExternal_Click(object sender, RoutedEventArgs e)
        {
            var term = txt_Search.Text?.Trim();
            if (string.IsNullOrWhiteSpace(term)) return;

            var lidarr = new LidarrHelper();
            try
            {
                var raw = await lidarr.LookupArtistsRawAsync(term);
                // Clear existing external items (treat external results separately)
                _externalItems.Clear();

                // Parse raw JSON objects and add to external list
                foreach (var r in raw)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(r);
                        var name = doc.RootElement.TryGetProperty("artistName", out var an) && an.ValueKind == JsonValueKind.String ? an.GetString() ?? r : r;
                        var item = new ManualMatchItem { IsExternal = true, DisplayName = name, RawJson = r };
                        _externalItems.Add(item);
                    }
                    catch
                    {
                        // skip malformed
                    }
                }

                // Show external items in the list
                list_Artists.ItemsSource = _externalItems;

                // Update context to indicate external results
                txt_Context.Text = string.IsNullOrWhiteSpace(term) ? "External Results" : $"External results for: {term}";

                if (_externalItems.Count ==0)
                    MessageBox.Show("No external results found.", "No Results", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"External search failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void btn_Ok_Click(object sender, RoutedEventArgs e)
        {
            if (list_Artists.SelectedItem is ManualMatchItem mi)
            {
                if (!mi.IsExternal && mi.Artist != null)
                {
                    SelectedArtist = mi.Artist;
                    DialogResult = true;
                    return;
                }

                if (mi.IsExternal && !string.IsNullOrWhiteSpace(mi.RawJson))
                {
                    // Convert raw JSON to LidarrArtist minimally (artistName + id if present)
                    try
                    {
                        //TODO - Actually do the import here. 


                        using var doc = JsonDocument.Parse(mi.RawJson);
                        var artist = new LidarrArtist();
                        if (doc.RootElement.TryGetProperty("artistName", out var an) && an.ValueKind == JsonValueKind.String)
                            artist.ArtistName = an.GetString() ?? string.Empty;
                        if (doc.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number)
                            artist.Id = id.GetInt32();

                        // Show messagebox indicating this is an external import
                        MessageBox.Show($"Importing external artist: {artist.ArtistName} (ID: {artist.Id})", "External Import", MessageBoxButton.OK, MessageBoxImage.Information);

                        SelectedArtist = artist;
                        DialogResult = true;
                        return;
                    }
                    catch
                    {
                        MessageBox.Show("Failed to parse selected external artist.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                }
            }
        }

        private void btn_Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void btn_Reset_Click(object sender, RoutedEventArgs e)
        {
            // Clear search and show original internal list
            txt_Search.Text = string.Empty;
            list_Artists.ItemsSource = _internalCandidates;
            _externalItems.Clear();

            // Restore context text
            txt_Context.Text = _originalContext;
        }
    }

    // Small helper model used by the ManualMatchWindow list
    public class ManualMatchItem
    {
        public bool IsExternal { get; set; }
        public string DisplayName { get; set; } = string.Empty;
        public string RawJson { get; set; } = string.Empty;
        public LidarrArtist? Artist { get; set; }
    }
}