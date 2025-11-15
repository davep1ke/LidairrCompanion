using LidairrCompanion.Helpers;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;

namespace LidairrCompanion
{
    public partial class ManualMatchWindow : Window
    {
        private readonly ObservableCollection<ManualMatchItem> _externalItems = new();
        private readonly List<ManualMatchItem> _internalItems = new();
        private readonly IList<LidarrArtist> _internalCandidates;
        private readonly string _originalContext;

        public LidarrArtist? SelectedArtist { get; private set; }

        // Busy state to prevent double clicks
        private bool _isBusy = false;

        public ManualMatchWindow(IEnumerable<LidarrArtist> candidates, string releasePath)
        {
            InitializeComponent();
            _originalContext = string.IsNullOrWhiteSpace(releasePath) ? "Internal Artists" : $"{releasePath} (Internal Artists)";
            txt_Context.Text = _originalContext;

            _internalCandidates = candidates.ToList();

            // Wrap internal candidates in ManualMatchItem so the list can show DisplayName for both internal and external results
            foreach (var c in _internalCandidates)
            {
                _internalItems.Add(new ManualMatchItem { IsExternal = false, DisplayName = c.ArtistName, Artist = c });
            }

            // Initially show internal items
            list_Artists.ItemsSource = _internalItems;

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
            if (idx > 0)
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

            // If external results are currently visible or we have external items and a search term, show filtered external
            if (list_Artists.ItemsSource == _externalItems || (_externalItems.Count > 0 && !string.IsNullOrWhiteSpace(term)))
            {
                var filtered = _externalItems.Where(i => i.DisplayName.Contains(term, System.StringComparison.OrdinalIgnoreCase)).ToList();
                list_Artists.ItemsSource = filtered;
            }
            else
            {
                var filtered = _internalItems.Where(i => i.DisplayName.Contains(term, System.StringComparison.OrdinalIgnoreCase)).ToList();
                list_Artists.ItemsSource = filtered;
            }
        }

        private void SetBusy(string message)
        {
            _isBusy = true;
            Dispatcher.Invoke(() =>
            {
                txt_Status.Text = message;
                btn_SearchExternal.IsEnabled = false;
                btn_Ok.IsEnabled = false;
                btn_Reset.IsEnabled = false;
                btn_Cancel.IsEnabled = false;
                txt_Search.IsEnabled = false;
                list_Artists.IsEnabled = false;
            });
        }

        private void ClearBusy()
        {
            _isBusy = false;
            Dispatcher.Invoke(() =>
            {
                txt_Status.Text = string.Empty;
                btn_SearchExternal.IsEnabled = true;
                btn_Reset.IsEnabled = true;
                btn_Cancel.IsEnabled = true;
                txt_Search.IsEnabled = true;
                list_Artists.IsEnabled = true;
                // Only enable OK if selection present and not external-processing
                btn_Ok.IsEnabled = list_Artists.SelectedItem != null;
            });
        }

        private async void btn_SearchExternal_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;

            var term = txt_Search.Text?.Trim();
            if (string.IsNullOrWhiteSpace(term)) return;

            SetBusy($"Searching external artists for: {term}");
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

                if (_externalItems.Count == 0)
                    MessageBox.Show("No external results found.", "No Results", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"External search failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                ClearBusy();
            }
        }

        private async void btn_Ok_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;

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
                    SetBusy("Importing external artist...");
                    try
                    {
                        // Convert raw JSON to LidarrArtist minimally (artistName + id if present)
                        using var doc = JsonDocument.Parse(mi.RawJson);
                        var artistName = doc.RootElement.TryGetProperty("artistName", out var an) && an.ValueKind == JsonValueKind.String ? an.GetString() ?? string.Empty : string.Empty;

                        // Try to get a foreignArtistId - fall back to id or generate GUID
                        string foreignId;
                        if (doc.RootElement.TryGetProperty("foreignArtistId", out var fid) && fid.ValueKind == JsonValueKind.String)
                            foreignId = fid.GetString()!;
                        else if (doc.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                            foreignId = id.GetString()!;
                        else if (doc.RootElement.TryGetProperty("id", out var idn) && idn.ValueKind == JsonValueKind.Number)
                            foreignId = idn.GetInt32().ToString();
                        else
                            foreignId = System.Guid.NewGuid().ToString();

                        var folder = string.IsNullOrWhiteSpace(artistName) ? "Unknown" : artistName;

                        // Create artist in Lidarr by POSTing constructed values (payload construction moved into helper)
                        var lidarr = new LidarrHelper();
                        var created = await lidarr.CreateArtistAsync(artistName, foreignId, folder, "/mnt/Music/Albums", qualityProfileId: 1, metadataProfileId: 5, monitored: true, searchForMissingAlbums: true);

                        if (created != null)
                        {
                            SelectedArtist = created;
                        }
                        else
                        {
                            // Fallback: if create failed to parse, return a minimal object
                            var fallback = new LidarrArtist { ArtistName = artistName, Id = 0 };
                            SelectedArtist = fallback;
                        }

                        DialogResult = true;
                        return;
                    }
                    catch (System.Exception ex)
                    {
                        MessageBox.Show($"Failed to parse selected external artist: {ex.Message}\n\n{ex}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                    finally
                    {
                        ClearBusy();
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
            list_Artists.ItemsSource = _internalItems;
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