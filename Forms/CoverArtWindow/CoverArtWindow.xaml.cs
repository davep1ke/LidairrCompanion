using LidarrCompanion.Helpers;
using LidarrCompanion.Models;
using LidarrCompanion.Services;
using Microsoft.Win32;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;


namespace LidarrCompanion
{
    /// <summary>
    /// Window for managing cover art for files that are missing it before import
    /// </summary>
    public partial class CoverArtWindow : Window
    {
        private readonly ObservableCollection<CoverArtItem> _items = new ObservableCollection<CoverArtItem>();
        private CoverArtItem? _currentItem;
        private BitmapImage? _currentImage;
        
        /// <summary>
        /// True if the user completed the cover art process, false if aborted
        /// </summary>
        public bool IsCompleted { get; private set; }

        public CoverArtWindow()
        {
            InitializeComponent();
            lst_Files.ItemsSource = _items;
            
            Loaded += CoverArtWindow_Loaded;
            
            // Add keyboard handler for paste
            KeyDown += CoverArtWindow_KeyDown;
        }

        private void CoverArtWindow_KeyDown(object sender, KeyEventArgs e)
        {
            // Ctrl+V to paste image from clipboard
            if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                try
                {
                    if (Clipboard.ContainsImage())
                    {
                        var image = Clipboard.GetImage();
                        if (image != null)
                        {
                            LoadImageFromBitmap(image);
                            Logger.Log("Image pasted from clipboard", LogSeverity.Verbose);
                        }
                    }
                    else
                    {
                        Logger.Log("Clipboard does not contain an image", LogSeverity.Verbose);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"Failed to paste image: {ex.Message}", LogSeverity.Low);
                }
            }
        }

        private async void CoverArtWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Helpers.ThemeManager.ApplyTheme(this);
            
            // Initialize WebView2
            try
            {
                await webBrowser.EnsureCoreWebView2Async(null);
                webBrowser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
                webBrowser.CoreWebView2.Settings.AreDevToolsEnabled = false;
                
                Logger.Log("WebView2 initialized successfully", LogSeverity.Verbose);
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to initialize WebView2: {ex.Message}", LogSeverity.High, new { Error = ex.Message });
                MessageBox.Show($"Failed to initialize web browser: {ex.Message}\n\nYou may need to install the WebView2 Runtime from Microsoft.", 
                    "Browser Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>
        /// Load files from proposed actions that need cover art verification
        /// </summary>
        /// <param name="actions">Proposed actions to check</param>
        /// <param name="destinations">Import destinations configuration</param>
        /// <param name="isTestMode">If true, skips destination validation and allows testing with any files</param>
        public void LoadActions(System.Collections.Generic.List<ProposedAction> actions, System.Collections.Generic.List<ImportDestination> destinations, bool isTestMode = false)
        {
            if (actions == null || !actions.Any())
            {
                if (!isTestMode)
                {
                    Close();
                    return;
                }
                else
                {
                    MessageBox.Show("No files provided to check for cover art.", "No Files", MessageBoxButton.OK, MessageBoxImage.Information);
                    Close();
                    return;
                }
            }

            _items.Clear();

            foreach (var action in actions)
            {
                // Resolve the file path
                var filePath = FileOperationsHelper.ResolveMappedPath(action.Path, SettingKey.LidarrImportPath, true);
                
                if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                {
                    Logger.Log($"File not found for cover art check: {action.Path}", LogSeverity.Low);
                    continue;
                }

                // Check if file already has cover art
                var existingCoverArt = FileAndAudioService.ExtractCoverArt(filePath);
                var hasCoverArt = existingCoverArt != null;

                // Extract metadata for search
                var (artist, album, _) = FileAndAudioService.ExtractMetadata(filePath);

                var item = new CoverArtItem
                {
                    Action = action,
                    FileName = Path.GetFileName(filePath),
                    FilePath = filePath,
                    ReleaseName = action.OriginalRelease ?? string.Empty,
                    DestinationName = action.DestinationName ?? (isTestMode ? "Test" : "Import"),
                    HasCoverArt = hasCoverArt,
                    CoverArtPreview = existingCoverArt,
                    Artist = artist,
                    Album = album
                };

                _items.Add(item);

                Logger.Log($"Added file to cover art check: {item.FileName}, HasCoverArt={hasCoverArt}", LogSeverity.Verbose);
            }

            if (!_items.Any())
            {
                MessageBox.Show("No valid audio files found to check for cover art.", "No Valid Files", MessageBoxButton.OK, MessageBoxImage.Information);
                Close();
                return;
            }

            UpdateStatus();
            
            // In test mode, show title indicator
            if (isTestMode)
            {
                Title = "Cover Art Management (Test Mode)";
            }
            
            // Select first item without cover art, or first item
            var firstMissing = _items.FirstOrDefault(i => !i.HasCoverArt);
            if (firstMissing != null)
            {
                lst_Files.SelectedItem = firstMissing;
            }
            else if (_items.Any())
            {
                lst_Files.SelectedItem = _items.First();
            }
        }

        private void FilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (lst_Files.SelectedItem is CoverArtItem item)
            {
                LoadCurrentItem(item);
            }
        }

        private void LoadCurrentItem(CoverArtItem item)
        {
            _currentItem = item;
            _currentImage = null;

            // Update file info display
            txt_Artist.Text = item.Artist ?? "-";
            txt_Album.Text = item.Album ?? "-";
            txt_FileName.Text = item.FileName;

            // Update image preview
            if (item.HasCoverArt && item.CoverArtPreview != null)
            {
                img_Preview.Source = item.CoverArtPreview;
                txt_DropHint.Visibility = Visibility.Collapsed;
                
                // Display dimensions if available
                if (item.CoverArtPreview is BitmapSource bitmap)
                {
                    txt_ImageDimensions.Text = $"{bitmap.PixelWidth} × {bitmap.PixelHeight}";
                    txt_ImageDimensions.Visibility = Visibility.Visible;
                }
            }
            else
            {
                img_Preview.Source = null;
                txt_DropHint.Visibility = Visibility.Visible;
                txt_ImageDimensions.Visibility = Visibility.Collapsed;
            }

            // Build search query
            var searchQuery = BuildSearchQuery(item);
            txt_Search.Text = searchQuery;

            // Auto-search
            PerformSearch(searchQuery);

            UpdateButtonStates();
        }

        private string BuildSearchQuery(CoverArtItem item)
        {
            var parts = new System.Collections.Generic.List<string>();

            // Start with filename (without extension)
            if (!string.IsNullOrWhiteSpace(item.FileName))
            {
                var fileNameWithoutExt = Path.GetFileNameWithoutExtension(item.FileName);
                parts.Add(fileNameWithoutExt);
            }

            // Get all words from filename for comparison (lowercase)
            var filenameWords = new HashSet<string>(
                (parts.Count > 0 ? parts[0] : string.Empty)
                    .Split(new[] { ' ', '_', '-', '.', '(', ')', '[', ']' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(w => w.ToLowerInvariant()),
                StringComparer.OrdinalIgnoreCase
            );

            // Only add Artist if its words aren't already in filename
            if (!string.IsNullOrWhiteSpace(item.Artist))
            {
                var artistWords = item.Artist.Split(new[] { ' ', '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
                if (!artistWords.All(word => filenameWords.Contains(word.ToLowerInvariant())))
                {
                    parts.Add(item.Artist);
                }
            }
            
            // Only add Album if its words aren't already in filename
            if (!string.IsNullOrWhiteSpace(item.Album))
            {
                var albumWords = item.Album.Split(new[] { ' ', '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
                if (!albumWords.All(word => filenameWords.Contains(word.ToLowerInvariant())))
                {
                    parts.Add(item.Album);
                }
            }
            else if (!string.IsNullOrWhiteSpace(item.ReleaseName))
            {
                // Only add ReleaseName if its words aren't already in filename
                var releaseWords = item.ReleaseName.Split(new[] { ' ', '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
                if (!releaseWords.All(word => filenameWords.Contains(word.ToLowerInvariant())))
                {
                    parts.Add(item.ReleaseName);
                }
            }

            return string.Join(" ", parts);
        }

        private void btn_Search_Click(object sender, RoutedEventArgs e)
        {
            PerformSearch(txt_Search.Text);
        }

        private void txt_Search_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                PerformSearch(txt_Search.Text);
            }
        }

        private void PerformSearch(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return;

            if (webBrowser.CoreWebView2 == null)
            {
                Logger.Log("WebView2 not initialized, cannot perform search", LogSeverity.Low);
                return;
            }

            try
            {
                // Clean up query
                query = query.Replace("&", "").Replace("_", " ").Replace("-", " ");
                
                // Build Google Images search URL
                var encodedQuery = Uri.EscapeDataString(query);
                var searchUrl = $"https://www.google.com/search?safe=off&tbm=isch&q={encodedQuery}";
                
                webBrowser.CoreWebView2.Navigate(searchUrl);
                Logger.Log($"Searching for: {query}", LogSeverity.Verbose, new { Query = query });
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to perform search: {ex.Message}", LogSeverity.Medium, new { Query = query, Error = ex.Message });
            }
        }

        private void ImageDropZone_DragOver(object sender, DragEventArgs e)
        {
            // Accept everything - be as permissive as possible
            e.Effects = DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link;
            e.Handled = true;
        }

        private void ImageDropZone_Drop(object sender, DragEventArgs e)
        {
            try
            {
                // Log all available formats for debugging
                var formats = e.Data.GetFormats();
                Logger.Log($"Drop received with {formats.Length} format(s): {string.Join(", ", formats)}", LogSeverity.Verbose);

                // Try each format systematically
                foreach (var format in formats)
                {
                    try
                    {
                        var data = e.Data.GetData(format);
                        Logger.Log($"Format '{format}': {data?.GetType().Name ?? "null"}", LogSeverity.Verbose);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Failed to get data for format '{format}': {ex.Message}", LogSeverity.Verbose);
                    }
                }

                // Try Bitmap first
                if (e.Data.GetDataPresent(DataFormats.Bitmap))
                {
                    try
                    {
                        var bitmap = e.Data.GetData(DataFormats.Bitmap) as BitmapSource;
                        if (bitmap != null)
                        {
                            Logger.Log("Processing bitmap drop", LogSeverity.Verbose);
                            LoadImageFromBitmap(bitmap);
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Failed to process bitmap: {ex.Message}", LogSeverity.Low);
                    }
                }

                // Try HTML formats
                string? html = null;
                
                foreach (var htmlFormat in new[] { "text/html", "HTML Format", "Html" })
                {
                    if (e.Data.GetDataPresent(htmlFormat))
                    {
                        try
                        {
                            var obj = e.Data.GetData(htmlFormat);
                            html = ExtractHtmlString(obj);
                            if (!string.IsNullOrEmpty(html))
                            {
                                Logger.Log($"Got HTML from format '{htmlFormat}', length: {html.Length}", LogSeverity.Verbose);
                                break;
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"Failed to extract HTML from '{htmlFormat}': {ex.Message}", LogSeverity.Verbose);
                        }
                    }
                }

                if (!string.IsNullOrEmpty(html))
                {
                    Logger.Log($"Processing HTML drop data (first 500 chars): {html.Substring(0, Math.Min(500, html.Length))}", LogSeverity.Verbose);
                    
                    // Try to extract image src from HTML
                    var match = new Regex(@"<img[^>]*src\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase).Match(html);
                    if (match.Success)
                    {
                        var imageUrl = match.Groups[1].Value;
                        Logger.Log($"Found image URL in HTML: {imageUrl}", LogSeverity.Verbose);
                        try
                        {
                            LoadImageFromUri(new Uri(imageUrl));
                            return;
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"Failed to load image from extracted URL: {ex.Message}", LogSeverity.Low);
                        }
                    }

                    // Try to find any URL that looks like an image
                    var urlMatches = new Regex(@"https?://[^\s<>""]+\.(?:jpg|jpeg|png|gif|webp|bmp)", RegexOptions.IgnoreCase).Matches(html);
                    if (urlMatches.Count > 0)
                    {
                        Logger.Log($"Found {urlMatches.Count} potential image URLs in HTML", LogSeverity.Verbose);
                        foreach (Match urlMatch in urlMatches)
                        {
                            try
                            {
                                var imageUrl = urlMatch.Value;
                                Logger.Log($"Trying image URL: {imageUrl}", LogSeverity.Verbose);
                                if (LoadImageFromUri(new Uri(imageUrl)))
                                    return;
                            }
                            catch (Exception ex)
                            {
                                Logger.Log($"Failed to load from URL: {ex.Message}", LogSeverity.Verbose);
                            }
                        }
                    }

                    // Try Google Images encoded URL style
                    var encodedMatch = new Regex(@"(url|imgurl)=([^&\s]+)", RegexOptions.IgnoreCase).Match(html);
                    if (encodedMatch.Success)
                    {
                        try
                        {
                            var urlString = Uri.UnescapeDataString(encodedMatch.Groups[2].Value);
                            Logger.Log($"Found encoded URL: {urlString}", LogSeverity.Verbose);
                            var uri = new Uri(urlString);
                            if (LoadImageFromUri(uri))
                                return;
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"Failed to load from encoded URL: {ex.Message}", LogSeverity.Verbose);
                        }
                    }
                }

                // Try FileDrop as last resort
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    try
                    {
                        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                        if (files != null && files.Length > 0)
                        {
                            Logger.Log($"Processing file drop: {files[0]}", LogSeverity.Verbose);
                            var ext = Path.GetExtension(files[0]).ToLowerInvariant();
                            if (new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp" }.Contains(ext))
                            {
                                LoadImageFromUri(new Uri(files[0]));
                                return;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Failed to process file drop: {ex.Message}", LogSeverity.Low);
                    }
                }

                // No supported format found
                Logger.Log("No supported image format found in drop data. Available formats: " + string.Join(", ", formats), LogSeverity.Low);
                MessageBox.Show($"Could not extract image from dropped data.\n\nAvailable formats: {string.Join(", ", formats)}\n\nTry:\n1. Right-click the image and 'Copy Image'\n2. Use Ctrl+V to paste into the drop zone", 
                    "Drop Failed", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Logger.Log($"Error handling dropped image: {ex.Message}", LogSeverity.Medium, new { Error = ex.Message, Stack = ex.StackTrace });
                MessageBox.Show($"Failed to process drop: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private string? ExtractHtmlString(object obj)
        {
            if (obj is string str)
            {
                return str;
            }
            else if (obj is MemoryStream ms)
            {
                byte[] buffer = new byte[ms.Length];
                ms.Read(buffer, 0, (int)ms.Length);
                
                // Detect unicode
                if (buffer.Length > 1 && buffer[1] == (byte)0)
                {
                    return System.Text.Encoding.Unicode.GetString(buffer);
                }
                else
                {
                    return System.Text.Encoding.ASCII.GetString(buffer);
                }
            }
            return null;
        }

        private bool LoadImageFromUri(Uri uri)
        {
            try
            {
                Logger.Log($"Downloading image from: {uri}", LogSeverity.Verbose);

                using (var webClient = new System.Net.WebClient())
                {
                    System.Net.ServicePointManager.Expect100Continue = true;
                    System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12;

                    byte[] data = webClient.DownloadData(uri);
                    
                    using (var ms = new MemoryStream(data))
                    {
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.StreamSource = ms;
                        bitmap.EndInit();
                        bitmap.Freeze();

                        _currentImage = bitmap;
                        img_Preview.Source = bitmap;
                        txt_DropHint.Visibility = Visibility.Collapsed;
                        
                        // Display image dimensions
                        txt_ImageDimensions.Text = $"{bitmap.PixelWidth} × {bitmap.PixelHeight}";
                        txt_ImageDimensions.Visibility = Visibility.Visible;
                        
                        UpdateButtonStates();
                        Logger.Log($"Successfully loaded image from URI: {bitmap.PixelWidth}x{bitmap.PixelHeight}", LogSeverity.Verbose);
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to download image from URI: {ex.Message}", LogSeverity.Low, new { Uri = uri.ToString(), Error = ex.Message });
                return false;
            }
        }



        private void LoadImageFromBitmap(BitmapSource bitmap)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            using (var ms = new MemoryStream())
            {
                encoder.Save(ms);
                ms.Position = 0;

                var bitmapImage = new BitmapImage();
                bitmapImage.BeginInit();
                bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                bitmapImage.StreamSource = ms;
                bitmapImage.EndInit();
                bitmapImage.Freeze();

                _currentImage = bitmapImage;
                img_Preview.Source = bitmapImage;
                txt_DropHint.Visibility = Visibility.Collapsed;
                
                // Display image dimensions
                txt_ImageDimensions.Text = $"{bitmapImage.PixelWidth} × {bitmapImage.PixelHeight}";
                txt_ImageDimensions.Visibility = Visibility.Visible;
                
                UpdateButtonStates();
                Logger.Log($"Loaded image from bitmap: {bitmapImage.PixelWidth}x{bitmapImage.PixelHeight}", LogSeverity.Verbose);
            }
        }

        private void btn_ClearImage_Click(object sender, RoutedEventArgs e)
        {
            _currentImage = null;
            img_Preview.Source = null;
            txt_DropHint.Visibility = Visibility.Visible;
            txt_ImageDimensions.Visibility = Visibility.Collapsed;
            UpdateButtonStates();
        }

        private void btn_SaveAndNext_Click(object sender, RoutedEventArgs e)
        {
            if (_currentItem != null && _currentImage != null)
            {
                SaveCoverArt(_currentItem, _currentImage);
                SelectNextItem();
            }
        }

        private void btn_SkipToNext_Click(object sender, RoutedEventArgs e)
        {
            SelectNextItem();
        }

        private void SaveCoverArt(CoverArtItem item, BitmapImage image)
        {
            try
            {
                Logger.Log($"Saving cover art to: {item.FilePath}", LogSeverity.Low, new { FileName = item.FileName });

                // Use the service to save cover art
                if (!FileAndAudioService.SaveCoverArt(item.FilePath, image))
                {
                    throw new Exception("Failed to save cover art to file");
                }

                Logger.Log($"Cover art saved successfully: {item.FileName}", LogSeverity.Low);

                // Update item status
                item.HasCoverArt = true;
                item.CoverArtPreview = image;
                
                UpdateStatus();
                UpdateButtonStates();
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to save cover art: {ex.Message}", LogSeverity.High, new { FileName = item.FileName, Error = ex.Message });
                MessageBox.Show($"Failed to save cover art: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SelectNextItem()
        {
            var currentIndex = lst_Files.SelectedIndex;
            if (currentIndex < _items.Count - 1)
            {
                lst_Files.SelectedIndex = currentIndex + 1;
            }
            else
            {
                MessageBox.Show("No more files to process.", "End of List", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void UpdateStatus()
        {
            var totalFiles = _items.Count;
            var filesWithCover = _items.Count(i => i.HasCoverArt);
            var filesMissing = totalFiles - filesWithCover;

            txt_Status.Text = $"{filesWithCover}/{totalFiles} files have cover art ({filesMissing} remaining)";
        }

        private void UpdateButtonStates()
        {
            var hasImage = _currentImage != null;
            btn_SaveAndNext.IsEnabled = hasImage && _currentItem != null;
            
            // Enable Complete button only when all files have cover art
            btn_Complete.IsEnabled = _items.Any() && _items.All(i => i.HasCoverArt);
        }

        private void btn_Complete_Click(object sender, RoutedEventArgs e)
        {
            IsCompleted = true;
            DialogResult = true;
            Close();
        }

        private void btn_Abort_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "Are you sure you want to abort the import process?",
                "Confirm Abort",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                IsCompleted = false;
                DialogResult = false;
                Close();
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            // If window is being closed without explicit Complete/Abort, treat as abort
            if (DialogResult == null)
            {
                IsCompleted = false;
                DialogResult = false;
            }
            base.OnClosing(e);
        }
    }

    /// <summary>
    /// Represents a file item in the cover art management grid
    /// </summary>
    public class CoverArtItem : INotifyPropertyChanged
    {
        private bool _hasCoverArt;
        private ImageSource? _coverArtPreview;

        /// <summary>
        /// The proposed action this item represents
        /// </summary>
        public ProposedAction? Action { get; set; }

        /// <summary>
        /// File name without path
        /// </summary>
        public string FileName { get; set; } = string.Empty;

        /// <summary>
        /// Full file path for cover art operations
        /// </summary>
        public string FilePath { get; set; } = string.Empty;

        /// <summary>
        /// Release/album name
        /// </summary>
        public string ReleaseName { get; set; } = string.Empty;

        /// <summary>
        /// Destination name
        /// </summary>
        public string DestinationName { get; set; } = string.Empty;

        /// <summary>
        /// Artist name from metadata
        /// </summary>
        public string? Artist { get; set; }

        /// <summary>
        /// Album name from metadata
        /// </summary>
        public string? Album { get; set; }

        /// <summary>
        /// Whether the file has cover art
        /// </summary>
        public bool HasCoverArt
        {
            get => _hasCoverArt;
            set
            {
                if (_hasCoverArt != value)
                {
                    _hasCoverArt = value;
                    OnPropertyChanged(nameof(HasCoverArt));
                }
            }
        }

        /// <summary>
        /// Preview image of the cover art
        /// </summary>
        public ImageSource? CoverArtPreview
        {
            get => _coverArtPreview;
            set
            {
                if (_coverArtPreview != value)
                {
                    _coverArtPreview = value;
                    OnPropertyChanged(nameof(CoverArtPreview));
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}


