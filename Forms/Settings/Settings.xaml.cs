using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using LidarrCompanion.Models;
using System.Linq;

namespace LidarrCompanion
{
    /// <summary>
    /// Selects appropriate DataTemplate based on whether setting is a color pair or not
    /// This is much more efficient than nested visibility bindings for DataGrid virtualization
    /// </summary>
    public class SettingValueTemplateSelector : DataTemplateSelector
    {
        public DataTemplate? ColorPairTemplate { get; set; }
        public DataTemplate? SingleValueTemplate { get; set; }

        public override DataTemplate? SelectTemplate(object item, DependencyObject container)
        {
            if (item is SettingItem settingItem)
            {
                return settingItem.IsColorPair ? ColorPairTemplate : SingleValueTemplate;
            }
            return base.SelectTemplate(item, container);
        }
    }

    public partial class Settings : Window
    {
        public ObservableCollection<SettingItem> SettingsCollection { get; set; }
        public ObservableCollection<ImportDestination> ImportDestinationsCollection { get; set; }

        // Brushes
        private readonly Brush BrushGreen = new SolidColorBrush(Color.FromRgb(76,175,80));
        private readonly Brush BrushAmber = new SolidColorBrush(Color.FromRgb(255,193,7));
        private readonly Brush BrushRed = new SolidColorBrush(Color.FromRgb(244,67,54));
        private readonly Brush BrushLightGray = new SolidColorBrush(Color.FromRgb(211,211,211));
        private readonly Brush BrushGray = new SolidColorBrush(Color.FromRgb(169,169,169));

        private bool _isPositioning = false;
        private bool _initialLayoutDone = false;

        // Debounce timer to prevent excessive arrow repositioning during resize
        private DispatcherTimer? _positionArrowsTimer;

        // Track dynamic lines so we can remove them
        private System.Collections.Generic.List<Line> _dynamicLines = new System.Collections.Generic.List<Line>();
        private System.Collections.Generic.List<Polygon> _dynamicHeads = new System.Collections.Generic.List<Polygon>();

        // Mapping of line name to friendly label and involved setting keys
        private readonly System.Collections.Generic.Dictionary<string, (string friendly, SettingKey[] keys)> _lineInfo = new System.Collections.Generic.Dictionary<string, (string, SettingKey[])>(StringComparer.OrdinalIgnoreCase)
        {
            { "Line_SiftTracks_LidarrURL", ("Sift Tracks → Lidarr", new[]{ SettingKey.SiftFolder, SettingKey.LidarrURL, SettingKey.LidarrAPIKey }) },
            { "Line_LidarrURL_Releases", ("Lidarr → Releases", new[]{ SettingKey.LidarrURL, SettingKey.LidarrAPIKey }) },
            { "Line_Releases_Import", ("Releases → Import", new[]{ SettingKey.LidarrImportPath, SettingKey.LidarrImportPathLocal }) },
            { "Line_LidarrURL_Backup", ("Import → Backup", new[]{ SettingKey.LidarrImportPath, SettingKey.BackupFilesBeforeImport, SettingKey.BackupRootFolder }) },
            { "Line_Import_Copy", ("Import → Copy", new[]{ SettingKey.LidarrImportPath, SettingKey.CopyImportedFiles, SettingKey.CopyImportedFilesPath }) }
        };

        // Mapping boxes to friendly names + setting keys to highlight
        private readonly System.Collections.Generic.Dictionary<string, (string friendly, SettingKey[] keys)> _boxInfo = new System.Collections.Generic.Dictionary<string, (string, SettingKey[])>(StringComparer.OrdinalIgnoreCase)
        {
            { "Box_SiftTracks", ("Sift Tracks", new[]{ SettingKey.SiftFolder }) },
            { "Box_LidarrURL", ("Lidarr URL", new[]{ SettingKey.LidarrURL, SettingKey.LidarrAPIKey }) },
            { "Box_LidarrReleases", ("Lidarr Releases", new[]{ SettingKey.LidarrImportPath, SettingKey.LidarrImportPathLocal }) },
            { "Box_LidarrImport", ("Lidarr Library", new[]{ SettingKey.LidarrLibraryPath, SettingKey.LidarrLibraryPathLocal }) },
            { "Box_BackupRootFolder", ("Backup Root", new[]{ SettingKey.BackupRootFolder, SettingKey.BackupFilesBeforeImport }) },
            { "Box_CopyImportedFiles", ("Copy Files", new[]{ SettingKey.CopyImportedFiles, SettingKey.CopyImportedFilesPath }) }
        };

        // Keep track of previously hovered element visual state
        private Line? _prevHoveredLine = null;
        private Brush? _prevLineStroke = null;
        private double _prevLineThickness = 0;
        private Border? _prevHoveredBox = null;
        private Brush? _prevBoxBorderBrush = null;
        private Thickness _prevBoxBorderThickness = new Thickness(0);

        public Settings()
        {
            SettingsCollection = AppSettings.Current.ToCollection();
            ImportDestinationsCollection = new ObservableCollection<ImportDestination>(AppSettings.Current.ImportDestinations);
            
            foreach (var dest in ImportDestinationsCollection)
                dest.PropertyChanged += Destination_PropertyChanged;
            
            DataContext = this;
            InitializeComponent();
            
            DynamicDestinationBoxes.ItemsSource = ImportDestinationsCollection;
            
            ImportDestinationsCollection.CollectionChanged += (s, e) => 
            {
                if (e.NewItems != null)
                    foreach (ImportDestination dest in e.NewItems)
                        dest.PropertyChanged += Destination_PropertyChanged;
                
                if (e.OldItems != null)
                    foreach (ImportDestination dest in e.OldItems)
                        dest.PropertyChanged -= Destination_PropertyChanged;
                
                Dispatcher.BeginInvoke(new Action(() => RefreshStatusPanel()), System.Windows.Threading.DispatcherPriority.Background);
            };

            Loaded += Settings_Loaded;
            SizeChanged += Settings_SizeChanged;
            LayoutUpdated += InitialLayoutUpdatedHandler;

            // Initialize debounce timer for arrow positioning
            _positionArrowsTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(150) // Wait 150ms after last resize before redrawing
            };
            _positionArrowsTimer.Tick += (s, e) =>
            {
                _positionArrowsTimer.Stop();
                PositionArrows();
            };
        }

        private void Settings_SizeChanged(object? sender, SizeChangedEventArgs e)
        {
            // Restart the timer - only draws arrows after user stops resizing for 150ms
            _positionArrowsTimer?.Stop();
            _positionArrowsTimer?.Start();
        }

        private void InitialLayoutUpdatedHandler(object? sender, EventArgs e)
        {
            if (_initialLayoutDone) return;
            _initialLayoutDone = true;
            LayoutUpdated -= InitialLayoutUpdatedHandler;
            PositionArrows();
        }

        private void Settings_Loaded(object sender, RoutedEventArgs e)
        {
            RefreshStatusPanel();
            Helpers.ThemeManager.ApplyTheme(this);
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);
            AppSettings.Current.UpdateFromCollection(SettingsCollection);
            AppSettings.Current.ImportDestinations = ImportDestinationsCollection.ToList();
            AppSettings.Save();
        }

        private void SettingsGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            if (e.EditAction != DataGridEditAction.Commit) return;

            CommitEditingElement(e.EditingElement);
            AppSettings.Current.UpdateFromCollection(SettingsCollection);
            AppSettings.Save();
            Dispatcher.BeginInvoke(new Action(() => RefreshStatusPanel()), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void list_ImportDestinations_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            if (e.EditAction != DataGridEditAction.Commit) return;

            CommitEditingElement(e.EditingElement);
            Dispatcher.BeginInvoke(new Action(() => 
            {
                AppSettings.Current.ImportDestinations = ImportDestinationsCollection.ToList();
                AppSettings.Save();
                RefreshStatusPanel();
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void CommitEditingElement(FrameworkElement element)
        {
            if (element is TextBox tb)
                tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            else if (element is CheckBox cb)
                cb.GetBindingExpression(CheckBox.IsCheckedProperty)?.UpdateSource();
        }

        private void btn_AddDestination_Click(object sender, RoutedEventArgs e)
        {
            var newDest = new ImportDestination
            {
                Name = $"Destination {ImportDestinationsCollection.Count + 1}",
                DestinationPath = string.Empty,
                BackupFiles = false,
                CopyFiles = false,
                RequireArtwork = false
            };
            ImportDestinationsCollection.Add(newDest);
            newDest.PropertyChanged += Destination_PropertyChanged;
            RefreshStatusPanel();
        }

        private void btn_RemoveDestination_Click(object sender, RoutedEventArgs e)
        {
            if (list_ImportDestinations.SelectedItem is not ImportDestination selected) return;
            
            var result = MessageBox.Show($"Remove destination '{selected.Name}'?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                selected.PropertyChanged -= Destination_PropertyChanged;
                ImportDestinationsCollection.Remove(selected);
                RefreshStatusPanel();
            }
        }

        private void Destination_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() => RefreshStatusPanel()), System.Windows.Threading.DispatcherPriority.Background);
        }

        #region Mouse Hover Handlers

        private void Arrow_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (sender is not Line l || string.IsNullOrWhiteSpace(l.Name)) return;

            var statusText = _lineInfo.TryGetValue(l.Name, out var info) ? info.friendly : l.Name;
            UpdateStatusText(statusText);
            ApplyLineHoverEffect(l);
            
            if (info.keys != null)
                HighlightSettings(info.keys, true);
        }

        private void Arrow_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            ClearStatusText();
            RemoveLineHoverEffect();
            HighlightSettings(null, false);
        }

        private void Box_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (sender is not Border b || string.IsNullOrWhiteSpace(b.Name)) return;

            var statusText = _boxInfo.TryGetValue(b.Name, out var info) ? info.friendly : b.Name;
            UpdateStatusText(statusText);
            ApplyBoxHoverEffect(b);
            
            if (info.keys != null)
                HighlightSettings(info.keys, true);
        }

        private void Box_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            ClearStatusText();
            RemoveBoxHoverEffect(sender as Border);
            HighlightSettings(null, false);
        }

        private void DestinationBox_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (sender is not Border box || box.Tag is not ImportDestination dest) return;

            UpdateStatusText($"Destination: {dest.Name}");
            ApplyBoxHoverEffect(box);
        }

        private void DestinationBox_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            ClearStatusText();
            RemoveBoxHoverEffect(sender as Border);
        }

        private void DynamicArrow_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (sender is not Line l || l.Tag is not string tag) return;

            var statusText = tag.Replace("Line_", "").Replace("_", " → ");
            UpdateStatusText(statusText);
            ApplyLineHoverEffect(l);
        }

        private void DynamicArrow_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            ClearStatusText();
            RemoveLineHoverEffect();
        }

        private void UpdateStatusText(string text)
        {
            if (this.FindName("ArrowStatusText") is TextBlock tb)
                tb.Text = text;
        }

        private void ClearStatusText()
        {
            if (this.FindName("ArrowStatusText") is TextBlock tb)
                tb.Text = string.Empty;
        }

        private void ApplyLineHoverEffect(Line line)
        {
            _prevHoveredLine = line;
            _prevLineStroke = line.Stroke;
            _prevLineThickness = line.StrokeThickness;
            line.Stroke = Brushes.Black;
            line.StrokeThickness = Math.Max(4, line.StrokeThickness + 2);
        }

        private void RemoveLineHoverEffect()
        {
            if (_prevHoveredLine != null)
            {
                _prevHoveredLine.Stroke = _prevLineStroke;
                _prevHoveredLine.StrokeThickness = _prevLineThickness;
                _prevHoveredLine = null;
            }
        }

        private void ApplyBoxHoverEffect(Border box)
        {
            _prevHoveredBox = box;
            _prevBoxBorderBrush = box.BorderBrush;
            _prevBoxBorderThickness = box.BorderThickness;
            box.BorderBrush = Brushes.Black;
            box.BorderThickness = new Thickness(2);
        }

        private void RemoveBoxHoverEffect(Border? box)
        {
            if (_prevHoveredBox != null && (box == null || _prevHoveredBox == box))
            {
                _prevHoveredBox.BorderBrush = _prevBoxBorderBrush;
                _prevHoveredBox.BorderThickness = _prevBoxBorderThickness;
                _prevHoveredBox = null;
            }
        }

        #endregion

        #region Settings Highlighting

        private void HighlightSettings(SettingKey[]? keys, bool highlight)
        {
            foreach (var si in SettingsCollection)
                si.IsHighlighted = false;

            if (keys == null || !highlight) return;

            var keyNames = keys.Select(k => k.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var si in SettingsCollection)
                if (keyNames.Contains(si.Name)) 
                    si.IsHighlighted = true;
        }

        #endregion

        #region Status Panel Refresh

        private void RefreshStatusPanel()
        {
            var darkMode = AppSettings.Current.GetTyped<bool>(SettingKey.DarkMode);

            // Update fixed box colors
            ApplyFixedBoxColors();

            // Update dynamic destination box colors
            ApplyDestinationBoxColors(darkMode);

            // Redraw all lines and arrows
            ClearDynamicLines();
            DrawAllLines();
            
            Dispatcher.BeginInvoke(new Action(() => PositionArrows()), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void ApplyFixedBoxColors()
        {
            var siftFolder = AppSettings.GetValue(SettingKey.SiftFolder);
            var lidarrUrl = AppSettings.GetValue(SettingKey.LidarrURL);
            var lidarrApiKey = AppSettings.GetValue(SettingKey.LidarrAPIKey);
            var backupRoot = AppSettings.GetValue(SettingKey.BackupRootFolder);
            var importPath = AppSettings.GetValue(SettingKey.LidarrImportPath);
            var importPathLocal = AppSettings.GetValue(SettingKey.LidarrImportPathLocal);
            var copyImportedPath = AppSettings.GetValue(SettingKey.CopyImportedFilesPath);
            var libraryPath = AppSettings.GetValue(SettingKey.LidarrLibraryPath);
            var libraryPathLocal = AppSettings.GetValue(SettingKey.LidarrLibraryPathLocal);
            var backupEnabled = AppSettings.Current.GetTyped<bool>(SettingKey.BackupFilesBeforeImport);
            var copyImported = AppSettings.Current.GetTyped<bool>(SettingKey.CopyImportedFiles);

            SetBoxColor("Box_SiftTracks", GetPathBrush(siftFolder));
            SetBoxColor("Box_LidarrURL", (!string.IsNullOrWhiteSpace(lidarrUrl) && !string.IsNullOrWhiteSpace(lidarrApiKey)) ? BrushGreen : BrushRed);
            SetBoxColor("Box_LidarrReleases", GetMappedPathBrush(importPath, importPathLocal));
            SetBoxColor("Box_LidarrImport", GetMappedPathBrush(libraryPath, libraryPathLocal));
            SetBoxColor("Box_BackupRootFolder", GetEnabledPathBrush(backupEnabled, backupRoot));
            SetBoxColor("Box_CopyImportedFiles", GetEnabledPathBrush(copyImported, copyImportedPath, BrushGray));
        }

        private void SetBoxColor(string boxName, Brush brush)
        {
            if (this.FindName(boxName) is Border box)
                box.Background = brush;
        }

        private Brush GetPathBrush(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return BrushRed;
            return Directory.Exists(path) ? BrushGreen : BrushAmber;
        }

        private Brush GetMappedPathBrush(string? serverPath, string? localPath)
        {
            if (string.IsNullOrWhiteSpace(serverPath) || string.IsNullOrWhiteSpace(localPath)) return BrushRed;
            return Directory.Exists(localPath) ? BrushGreen : BrushAmber;
        }

        private Brush GetEnabledPathBrush(bool enabled, string? path, Brush? disabledBrush = null)
        {
            if (!enabled) return disabledBrush ?? BrushLightGray;
            if (string.IsNullOrWhiteSpace(path)) return BrushRed;
            return Directory.Exists(path) ? BrushGreen : BrushAmber;
        }

        private void ApplyDestinationBoxColors(bool darkMode)
        {
            var destBoxes = FindDestinationBoxes();
            foreach (var (destName, border) in destBoxes)
            {
                var dest = ImportDestinationsCollection.FirstOrDefault(d => d.Name == destName);
                if (dest == null) continue;

                if (string.IsNullOrWhiteSpace(dest.DestinationPath) || !Directory.Exists(dest.DestinationPath))
                {
                    border.Background = BrushRed;
                }
                else
                {
                    var colorKey = darkMode ? dest.ColorDark : dest.Color;
                    border.Background = TryParseColorBrush(colorKey) ?? BrushGreen;
                }
            }
        }

        private Brush? TryParseColorBrush(string colorKey)
        {
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(colorKey);
                return new SolidColorBrush(color);
            }
            catch
            {
                return null;
            }
        }

        private void DrawAllLines()
        {
            // Get brushes for all connection points
            var brushes = GetConnectionBrushes();

            // Draw static lines
            DrawStaticLines(brushes);

            // Draw dynamic destination lines
            DrawDynamicDestinationLines(brushes);
        }

        private System.Collections.Generic.Dictionary<string, Brush> GetConnectionBrushes()
        {
            var siftFolder = AppSettings.GetValue(SettingKey.SiftFolder);
            var lidarrUrl = AppSettings.GetValue(SettingKey.LidarrURL);
            var lidarrApiKey = AppSettings.GetValue(SettingKey.LidarrAPIKey);
            var backupRoot = AppSettings.GetValue(SettingKey.BackupRootFolder);
            var importPath = AppSettings.GetValue(SettingKey.LidarrImportPath);
            var importPathLocal = AppSettings.GetValue(SettingKey.LidarrImportPathLocal);
            var copyImportedPath = AppSettings.GetValue(SettingKey.CopyImportedFilesPath);
            var libraryPath = AppSettings.GetValue(SettingKey.LidarrLibraryPath);
            var libraryPathLocal = AppSettings.GetValue(SettingKey.LidarrLibraryPathLocal);
            var backupEnabled = AppSettings.Current.GetTyped<bool>(SettingKey.BackupFilesBeforeImport);
            var copyEnabled = AppSettings.Current.GetTyped<bool>(SettingKey.CopyImportedFiles);

            return new System.Collections.Generic.Dictionary<string, Brush>
            {
                ["SiftTracks"] = GetPathBrush(siftFolder),
                ["Lidarr"] = (!string.IsNullOrWhiteSpace(lidarrUrl) && !string.IsNullOrWhiteSpace(lidarrApiKey)) ? BrushGreen : BrushRed,
                ["Releases"] = GetMappedPathBrush(importPath, importPathLocal),
                ["Library"] = GetMappedPathBrush(libraryPath, libraryPathLocal),
                ["Backup"] = GetEnabledPathBrush(backupEnabled, backupRoot),
                ["Copy"] = GetEnabledPathBrush(copyEnabled, copyImportedPath, BrushGray)
            };
        }

        #endregion

        #region Line Drawing

        private void ClearDynamicLines()
        {
            foreach (var line in _dynamicLines)
                ArrowsCanvas.Children.Remove(line);
            foreach (var head in _dynamicHeads)
                ArrowsCanvas.Children.Remove(head);
            
            _dynamicLines.Clear();
            _dynamicHeads.Clear();
        }

        private void DrawStaticLines(System.Collections.Generic.Dictionary<string, Brush> brushes)
        {
            DrawAndFillLine("Line_SiftTracks_LidarrURL", "Head_SiftTracks_LidarrURL", 
                MergeBrushes(brushes["SiftTracks"], brushes["Lidarr"]));
            DrawAndFillLine("Line_LidarrURL_Releases", "Head_LidarrURL_Releases", 
                MergeBrushes(brushes["Lidarr"], brushes["Releases"]));
            DrawAndFillLine("Line_Releases_Import", "Head_Releases_Import", 
                MergeBrushes(brushes["Releases"], brushes["Library"]));
            DrawAndFillLine("Line_LidarrURL_Backup", "Head_LidarrURL_Backup", 
                MergeBrushes(brushes["Library"], brushes["Backup"]));
            DrawAndFillLine("Line_Import_Copy", "Head_Import_Copy", 
                MergeBrushes(brushes["Library"], brushes["Copy"]));
        }

        private void DrawAndFillLine(string lineName, string headName, Brush brush)
        {
            if (this.FindName(lineName) is Line line)
            {
                line.Stroke = brush;
                if (this.FindName(headName) is Polygon head)
                    head.Fill = line.Stroke;
            }
        }

        private void DrawDynamicDestinationLines(System.Collections.Generic.Dictionary<string, Brush> brushes)
        {
            foreach (var dest in ImportDestinationsCollection)
            {
                var destBrush = string.IsNullOrWhiteSpace(dest.DestinationPath) ? BrushRed :
                               Directory.Exists(dest.DestinationPath) ? BrushGreen : BrushAmber;

                // Releases -> Destination
                CreateDynamicLine($"Line_Releases_{dest.Name}", $"Head_Releases_{dest.Name}", 
                    MergeBrushes(brushes["Releases"], destBrush));

                // Destination -> Backup (if enabled)
                if (dest.BackupFiles)
                    CreateDynamicLine($"Line_{dest.Name}_Backup", $"Head_{dest.Name}_Backup", 
                        MergeBrushes(destBrush, brushes["Backup"]));

                // Destination -> Copy (if enabled)
                if (dest.CopyFiles)
                    CreateDynamicLine($"Line_{dest.Name}_Copy", $"Head_{dest.Name}_Copy", 
                        MergeBrushes(destBrush, brushes["Copy"]));
            }
        }

        private void CreateDynamicLine(string lineTag, string headTag, Brush stroke)
        {
            var line = new Line
            {
                StrokeThickness = 3,
                Stroke = stroke,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Tag = lineTag
            };
            line.MouseEnter += DynamicArrow_MouseEnter;
            line.MouseLeave += DynamicArrow_MouseLeave;

            var head = new Polygon
            {
                Fill = stroke,
                Tag = headTag
            };

            ArrowsCanvas.Children.Add(line);
            ArrowsCanvas.Children.Add(head);
            _dynamicLines.Add(line);
            _dynamicHeads.Add(head);
        }

        private Brush MergeBrushes(Brush a, Brush b)
        {
            if (a == BrushRed || b == BrushRed) return BrushRed;
            if (a == BrushAmber || b == BrushAmber) return BrushAmber;
            if (a == BrushGray || b == BrushGray || a == BrushLightGray || b == BrushLightGray) return BrushGray;
            return BrushGreen;
        }

        #endregion

        #region Arrow Positioning

        private void PositionArrows()
        {
            if (_isPositioning || ArrowsCanvas == null) return;
            _isPositioning = true;

            try
            {
                var elements = BuildElementDictionary();
                DrawStaticConnections(elements);
                DrawDynamicConnections(elements);
            }
            finally
            {
                _isPositioning = false;
            }
        }

        private System.Collections.Generic.Dictionary<string, (Point center, Rect rect)> BuildElementDictionary()
        {
            var result = new System.Collections.Generic.Dictionary<string, (Point, Rect)>(StringComparer.OrdinalIgnoreCase);
            
            void AddBox(string key, string boxName)
            {
                var box = this.FindName(boxName) as UIElement ?? BoxesGrid as UIElement;
                result[key] = (GetCenter(box), GetElementRect(box));
            }

            AddBox("SiftTracks", "Box_SiftTracks");
            AddBox("Lidarr", "Box_LidarrURL");
            AddBox("Releases", "Box_LidarrReleases");
            AddBox("Import", "Box_LidarrImport");
            AddBox("Backup", "Box_BackupRootFolder");
            AddBox("Copy", "Box_CopyImportedFiles");

            foreach (var (name, border) in FindDestinationBoxes())
                result[$"Dest_{name}"] = (GetCenter(border), GetElementRect(border));

            return result;
        }

        private void DrawStaticConnections(System.Collections.Generic.Dictionary<string, (Point center, Rect rect)> elements)
        {
            ConnectElements(elements, "SiftTracks", "Lidarr", "Line_SiftTracks_LidarrURL", "Head_SiftTracks_LidarrURL");
            ConnectElements(elements, "Lidarr", "Releases", "Line_LidarrURL_Releases", "Head_LidarrURL_Releases");
            ConnectElements(elements, "Releases", "Import", "Line_Releases_Import", "Head_Releases_Import");
            ConnectElements(elements, "Import", "Backup", "Line_LidarrURL_Backup", "Head_LidarrURL_Backup");
            ConnectElements(elements, "Import", "Copy", "Line_Import_Copy", "Head_Import_Copy");
        }

        private void DrawDynamicConnections(System.Collections.Generic.Dictionary<string, (Point center, Rect rect)> elements)
        {
            foreach (var dest in ImportDestinationsCollection)
            {
                var destKey = $"Dest_{dest.Name}";
                
                var (lineRelDest, headRelDest) = FindDynamicLineAndHead($"Line_Releases_{dest.Name}", $"Head_Releases_{dest.Name}");
                if (lineRelDest != null)
                    ConnectElements(elements, "Releases", destKey, lineRelDest, headRelDest);

                if (dest.BackupFiles)
                {
                    var (lineDestBackup, headDestBackup) = FindDynamicLineAndHead($"Line_{dest.Name}_Backup", $"Head_{dest.Name}_Backup");
                    if (lineDestBackup != null)
                        ConnectElements(elements, destKey, "Backup", lineDestBackup, headDestBackup);
                }

                if (dest.CopyFiles)
                {
                    var (lineDestCopy, headDestCopy) = FindDynamicLineAndHead($"Line_{dest.Name}_Copy", $"Head_{dest.Name}_Copy");
                    if (lineDestCopy != null)
                        ConnectElements(elements, destKey, "Copy", lineDestCopy, headDestCopy);
                }
            }
        }

        private (Line?, Polygon?) FindDynamicLineAndHead(string lineTag, string headTag)
        {
            var line = _dynamicLines.FirstOrDefault(l => l.Tag as string == lineTag);
            var head = _dynamicHeads.FirstOrDefault(h => h.Tag as string == headTag);
            return (line, head);
        }

        private void ConnectElements(System.Collections.Generic.Dictionary<string, (Point center, Rect rect)> elements, 
            string srcKey, string dstKey, string lineName, string headName)
        {
            var line = this.FindName(lineName) as Line;
            var head = this.FindName(headName) as Polygon;
            ConnectElements(elements, srcKey, dstKey, line, head);
        }

        private void ConnectElements(System.Collections.Generic.Dictionary<string, (Point center, Rect rect)> elements, 
            string srcKey, string dstKey, Line? line, Polygon? head)
        {
            if (line == null) return;

            var (centerA, rectA) = elements.ContainsKey(srcKey) ? elements[srcKey] : (new Point(0, 0), Rect.Empty);
            var (centerB, rectB) = elements.ContainsKey(dstKey) ? elements[dstKey] : (new Point(0, 0), Rect.Empty);

            var start = GetRectIntersectionPoint(rectA, centerA, centerB, preferNearA: true) ?? centerA;
            var end = GetRectIntersectionPoint(rectB, centerA, centerB, preferNearA: false) ?? centerB;

            SetLine(line, head, start, end);
        }

        private Point GetCenter(UIElement el)
        {
            try
            {
                var transform = el.TransformToVisual(ArrowsCanvas);
                var topLeft = transform.Transform(new Point(0, 0));
                if (el is FrameworkElement fe)
                    return new Point(topLeft.X + fe.ActualWidth / 2, topLeft.Y + fe.ActualHeight / 2);
                return topLeft;
            }
            catch
            {
                return new Point(0, 0);
            }
        }

        private Rect GetElementRect(UIElement el)
        {
            try
            {
                if (el is FrameworkElement fe && ArrowsCanvas != null)
                {
                    var transform = fe.TransformToVisual(ArrowsCanvas);
                    var topLeft = transform.Transform(new Point(0, 0));
                    return new Rect(topLeft, new Size(fe.ActualWidth, fe.ActualHeight));
                }
            }
            catch { }
            return Rect.Empty;
        }

        private Point? GetRectIntersectionPoint(Rect rect, Point a, Point b, bool preferNearA)
        {
            if (rect.IsEmpty) return null;
            
            var dx1 = b.X - a.X;
            var dy1 = b.Y - a.Y;
            var candidates = new System.Collections.Generic.List<(Point pt, double t)>();

            Point[] corners = { new(rect.Left, rect.Top), new(rect.Right, rect.Top), 
                              new(rect.Right, rect.Bottom), new(rect.Left, rect.Bottom) };

            for (int i = 0; i < 4; i++)
            {
                var p1 = corners[i];
                var p2 = corners[(i + 1) % 4];
                var dx2 = p2.X - p1.X;
                var dy2 = p2.Y - p1.Y;

                var denom = dx1 * dy2 - dy1 * dx2;
                if (Math.Abs(denom) < 1e-9) continue;

                var t = ((p1.X - a.X) * dy2 - (p1.Y - a.Y) * dx2) / denom;
                var u = ((p1.X - a.X) * dy1 - (p1.Y - a.Y) * dx1) / denom;

                if (u >= -1e-9 && u <= 1.0 + 1e-9 && t >= -1e-9 && t <= 1.0 + 1e-9)
                {
                    candidates.Add((new Point(a.X + t * dx1, a.Y + t * dy1), t));
                }
            }

            if (candidates.Count == 0) return null;

            var best = preferNearA 
                ? candidates.Where(c => c.t >= 0).MinBy(c => c.t)
                : candidates.Where(c => c.t <= 1).MaxBy(c => c.t);

            return best.pt != default ? OffsetAlongLine(best.pt, a, b, preferNearA ? 4.0 : -4.0) : null;
        }

        private Point OffsetAlongLine(Point pt, Point a, Point b, double offset)
        {
            var vx = b.X - a.X;
            var vy = b.Y - a.Y;
            var len = Math.Sqrt(vx * vx + vy * vy);
            if (len < 1e-6) return pt;
            return new Point(pt.X + (vx / len) * offset, pt.Y + (vy / len) * offset);
        }

        private void SetLine(Line line, Polygon? head, Point a, Point b)
        {
            line.X1 = a.X;
            line.Y1 = a.Y;
            line.X2 = b.X;
            line.Y2 = b.Y;

            if (head == null) return;
            
            double angle = Math.Atan2(b.Y - a.Y, b.X - a.X);
            const double size = 10.0;
            head.Points = new PointCollection 
            {
                new Point(b.X, b.Y),
                new Point(b.X - size * Math.Cos(angle - Math.PI / 6), b.Y - size * Math.Sin(angle - Math.PI / 6)),
                new Point(b.X - size * Math.Cos(angle + Math.PI / 6), b.Y - size * Math.Sin(angle + Math.PI / 6))
            };
        }

        #endregion

        #region Helper Methods

        private System.Collections.Generic.Dictionary<string, Border> FindDestinationBoxes()
        {
            var result = new System.Collections.Generic.Dictionary<string, Border>();
            
            if (DynamicDestinationBoxes == null) return result;

            var presenter = FindVisualChild<ItemsPresenter>(DynamicDestinationBoxes);
            if (presenter == null) return result;

            var panel = FindVisualChild<Panel>(presenter);
            if (panel == null) return result;

            foreach (UIElement child in panel.Children)
            {
                if (child is ContentPresenter cp)
                {
                    var border = FindVisualChild<Border>(cp);
                    if (border?.Tag is ImportDestination dest)
                        result[dest.Name] = border;
                }
            }

            return result;
        }

        private T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) return null;

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typedChild)
                    return typedChild;

                var result = FindVisualChild<T>(child);
                if (result != null)
                    return result;
            }

            return null;
        }

        #endregion
    }
}