using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using LidarrCompanion.Models;
using System.Linq;

namespace LidarrCompanion
{
    public partial class Settings : Window
    {
        public ObservableCollection<SettingItem> SettingsCollection { get; set; }

        // Brushes
        private readonly Brush BrushGreen = new SolidColorBrush(Color.FromRgb(76,175,80));
        private readonly Brush BrushAmber = new SolidColorBrush(Color.FromRgb(255,193,7));
        private readonly Brush BrushRed = new SolidColorBrush(Color.FromRgb(244,67,54));
        private readonly Brush BrushLightGray = new SolidColorBrush(Color.FromRgb(211,211,211));
        private readonly Brush BrushGray = new SolidColorBrush(Color.FromRgb(169,169,169));

        // prevent re-entrant PositionArrows
        private bool _isPositioning = false;
        // whether initial layout has completed
        private bool _initialLayoutDone = false;

        public Settings()
        {
            // Create editable collection for DataGrid
            SettingsCollection = AppSettings.Current.ToCollection();
            DataContext = this;
            InitializeComponent();

            Loaded += Settings_Loaded;
            SizeChanged += (s, e) => PositionArrows();
            // Use a one-shot LayoutUpdated handler to perform initial positioning after WPF completes layout
            LayoutUpdated += InitialLayoutUpdatedHandler;
        }

        private void InitialLayoutUpdatedHandler(object? sender, EventArgs e)
        {
            // Ensure we only run once to avoid continuous calls
            if (_initialLayoutDone) return;
            _initialLayoutDone = true;

            // Unsubscribe to avoid repeated calls; SizeChanged will still reposition later
            LayoutUpdated -= InitialLayoutUpdatedHandler;

            // Do the initial positioning
            PositionArrows();
        }

        private void Settings_Loaded(object sender, RoutedEventArgs e)
        {
            RefreshStatusPanel();
            // PositionArrows will be called by InitialLayoutUpdatedHandler when layout is ready
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);
            // Update AppSettings from collection
            AppSettings.Current.UpdateFromCollection(SettingsCollection);
            AppSettings.Save();
        }

        private void SettingsGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            // Commit edits to collection
            if (e.EditAction == DataGridEditAction.Commit)
            {
                // Force the binding to update source
                var tb = e.EditingElement as TextBox;
                if (tb != null)
                {
                    var binding = tb.GetBindingExpression(TextBox.TextProperty);
                    binding?.UpdateSource();
                }

                // Update underlying settings and refresh visuals
                AppSettings.Current.UpdateFromCollection(SettingsCollection);
                AppSettings.Save();
                RefreshStatusPanel();
            }
        }

        // Mapping of line name to friendly label and involved setting keys
        private readonly System.Collections.Generic.Dictionary<string, (string friendly, SettingKey[] keys)> _lineInfo = new System.Collections.Generic.Dictionary<string, (string, SettingKey[])>(StringComparer.OrdinalIgnoreCase)
        {
            { "Line_LidarrURL_Releases", ("Lidarr → Releases", new[]{ SettingKey.LidarrURL, SettingKey.LidarrAPIKey }) },
            { "Line_Releases_Import", ("Releases → Import", new[]{ SettingKey.LidarrImportPath, SettingKey.LidarrImportPathLocal }) },
            { "Line_Releases_NotSelected", ("Releases → Not Selected", new[]{ SettingKey.LidarrImportPath, SettingKey.NotSelectedPath }) },
            { "Line_Releases_Defer", ("Releases → Defer", new[]{ SettingKey.DeferDestinationPath }) },
            { "Line_LidarrURL_Backup", ("Import → Backup", new[]{ SettingKey.LidarrImportPath, SettingKey.BackupFilesBeforeImport, SettingKey.BackupRootFolder }) },
            { "Line_Import_Copy", ("Import → Copy", new[]{ SettingKey.LidarrImportPath, SettingKey.CopyImportedFiles, SettingKey.CopyImportedFilesPath }) },
            { "Line_NotSelected_Copy", ("Not Selected → Copy", new[]{ SettingKey.NotSelectedPath, SettingKey.CopyNotSelectedFiles, SettingKey.CopyImportedFilesPath }) },
            { "Line_Defer_Backup", ("Defer → Backup", new[]{ SettingKey.DeferDestinationPath, SettingKey.BackupDeferredFiles, SettingKey.BackupRootFolder }) },
            { "Line_Defer_Copy", ("Defer → Copy", new[]{ SettingKey.DeferDestinationPath, SettingKey.CopyDeferredFiles, SettingKey.CopyImportedFilesPath }) },
            { "Line_NotSelected_Backup", ("Not Selected → Backup", new[]{ SettingKey.NotSelectedPath, SettingKey.BackupNotSelectedFiles, SettingKey.BackupRootFolder }) }
        };

        // Mapping boxes to friendly names + setting keys to highlight
        private readonly System.Collections.Generic.Dictionary<string, (string friendly, SettingKey[] keys)> _boxInfo = new System.Collections.Generic.Dictionary<string, (string, SettingKey[])>(StringComparer.OrdinalIgnoreCase)
        {
            { "Box_LidarrURL", ("Lidarr URL", new[]{ SettingKey.LidarrURL, SettingKey.LidarrAPIKey }) },
            { "Box_LidarrReleases", ("Lidarr Releases", new[]{ SettingKey.LidarrImportPath, SettingKey.LidarrImportPathLocal }) },
            { "Box_LidarrImport", ("Lidarr Import", new[]{ SettingKey.LidarrImportPath, SettingKey.LidarrImportPathLocal }) },
            { "Box_NotSelected", ("Not-Selected", new[]{ SettingKey.NotSelectedPath }) },
            { "Box_Defer", ("Defer", new[]{ SettingKey.DeferDestinationPath, SettingKey.CopyDeferredFiles, SettingKey.BackupDeferredFiles }) },
            { "Box_BackupRootFolder", ("Backup Root", new[]{ SettingKey.BackupRootFolder, SettingKey.BackupFilesBeforeImport }) },
            { "Box_CopyImportedFiles", ("Copy Files", new[]{ SettingKey.CopyImportedFiles, SettingKey.CopyImportedFilesPath }) }
        };

        // Keep track of previously hovered line visual state
        private Line? _prevHoveredLine = null;
        private Brush? _prevLineStroke = null;
        private double _prevLineThickness = 0;

        // Keep track of previously hovered box visual state
        private Border? _prevHoveredBox = null;
        private Brush? _prevBoxBorderBrush = null;
        private Thickness _prevBoxBorderThickness = new Thickness(0);

        private void Arrow_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            var tb = this.FindName("ArrowStatusText") as TextBlock;
            if (tb == null) return;
            if (!(sender is Line l) || string.IsNullOrWhiteSpace(l.Name))
            {
                tb.Text = string.Empty;
                return;
            }

            // Friendly name
            if (_lineInfo.TryGetValue(l.Name, out var info))
            {
                tb.Text = info.friendly;
            }
            else tb.Text = l.Name;

            // Outline effect: store previous stroke and thickness and set black thicker stroke
            _prevHoveredLine = l;
            _prevLineStroke = l.Stroke;
            _prevLineThickness = l.StrokeThickness;
            l.Stroke = Brushes.Black;
            l.StrokeThickness = Math.Max(4, l.StrokeThickness + 2);

            // Highlight involved settings
            HighlightSettings(info.keys, true);
        }

        private void Arrow_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            var tb = this.FindName("ArrowStatusText") as TextBlock;
            if (tb == null) return;
            tb.Text = string.Empty;

            if (_prevHoveredLine != null)
            {
                _prevHoveredLine.Stroke = _prevLineStroke;
                _prevHoveredLine.StrokeThickness = _prevLineThickness;
                _prevHoveredLine = null;
            }

            // Clear highlights
            HighlightSettings(null, false);
        }

        private void Box_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            var tb = this.FindName("ArrowStatusText") as TextBlock;
            if (tb == null) return;
            Border box = sender as Border ?? (this.FindName((sender as FrameworkElement)?.Name ?? string.Empty) as Border);
            if (box == null || string.IsNullOrWhiteSpace(box.Name))
            {
                tb.Text = string.Empty; return;
            }

            if (_boxInfo.TryGetValue(box.Name, out var info))
            {
                tb.Text = info.friendly;
                HighlightSettings(info.keys, true);
            }
            else tb.Text = box.Name;

            // Outline the box
            _prevHoveredBox = box;
            _prevBoxBorderBrush = box.BorderBrush;
            _prevBoxBorderThickness = box.BorderThickness;
            box.BorderBrush = Brushes.Black;
            box.BorderThickness = new Thickness(2);
        }

        private void Box_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            var tb = this.FindName("ArrowStatusText") as TextBlock;
            if (tb != null) tb.Text = string.Empty;

            if (_prevHoveredBox != null)
            {
                _prevHoveredBox.BorderBrush = _prevBoxBorderBrush;
                _prevHoveredBox.BorderThickness = _prevBoxBorderThickness;
                _prevHoveredBox = null;
            }

            HighlightSettings(null, false);
        }

        private void RefreshStatusPanel()
        {
            // Read settings
            var lidarrUrl = AppSettings.GetValue(SettingKey.LidarrURL);
            var lidarrApiKey = AppSettings.GetValue(SettingKey.LidarrAPIKey);
            var backupRoot = AppSettings.GetValue(SettingKey.BackupRootFolder);
            var importPath = AppSettings.GetValue(SettingKey.LidarrImportPath);
            var importPathLocal = AppSettings.GetValue(SettingKey.LidarrImportPathLocal);
            var notSelected = AppSettings.GetValue(SettingKey.NotSelectedPath);
            var copyImported = AppSettings.Current.GetTyped<bool>(SettingKey.CopyImportedFiles);
            var copyImportedPath = AppSettings.GetValue(SettingKey.CopyImportedFilesPath);
            var backupEnabled = AppSettings.Current.GetTyped<bool>(SettingKey.BackupFilesBeforeImport);

            // Defer settings
            var copyDeferred = AppSettings.Current.GetTyped<bool>(SettingKey.CopyDeferredFiles);
            var backupDeferred = AppSettings.Current.GetTyped<bool>(SettingKey.BackupDeferredFiles);
            var deferPath = AppSettings.GetValue(SettingKey.DeferDestinationPath);

            // NotSelected-specific flags
            var copyNotSelected = AppSettings.Current.GetTyped<bool>(SettingKey.CopyNotSelectedFiles);
            var backupNotSelected = AppSettings.Current.GetTyped<bool>(SettingKey.BackupNotSelectedFiles);

            // Determine brushes for each box
            var brushLidarr = (!string.IsNullOrWhiteSpace(lidarrUrl) && !string.IsNullOrWhiteSpace(lidarrApiKey)) ? BrushGreen : BrushRed;

            Brush brushBackup;
            if (!backupEnabled)
            {
                brushBackup = BrushLightGray;
            }
            else if (string.IsNullOrWhiteSpace(backupRoot))
            {
                brushBackup = BrushRed;
            }
            else if (Directory.Exists(backupRoot))
            {
                brushBackup = BrushGreen;
            }
            else
            {
                brushBackup = BrushAmber;
            }

            Brush brushImport;
            // Require that the server import path is set; but only mark green when a local mapping exists and is valid
            if (string.IsNullOrWhiteSpace(importPath))
            {
                brushImport = BrushRed;
            }
            else
            {
                // If a local mapping is provided and exists -> green
                if (!string.IsNullOrWhiteSpace(importPathLocal) && Directory.Exists(importPathLocal))
                {
                    brushImport = BrushGreen;
                }
                // If server path exists locally (maybe network-mounted) -> amber (accessible but mapping not configured)
                else if (Directory.Exists(importPath))
                {
                    brushImport = BrushAmber;
                }
                else
                {
                    brushImport = BrushRed;
                }
            }

            Brush brushNotSelected;
            if (string.IsNullOrWhiteSpace(notSelected)) brushNotSelected = BrushRed;
            else if (Directory.Exists(notSelected)) brushNotSelected = BrushGreen;
            else brushNotSelected = BrushAmber;

            Brush brushCopy;
            if (!copyImported)
            {
                brushCopy = BrushGray;
            }
            else if (string.IsNullOrWhiteSpace(copyImportedPath)) brushCopy = BrushRed;
            else if (Directory.Exists(copyImportedPath)) brushCopy = BrushGreen;
            else brushCopy = BrushAmber;

            // Defer brushes
            Brush brushDeferPath;
            if (string.IsNullOrWhiteSpace(deferPath)) brushDeferPath = BrushRed;
            else if (Directory.Exists(deferPath)) brushDeferPath = BrushGreen;
            else brushDeferPath = BrushAmber;

            Brush brushCopyDeferred;
            if (!copyDeferred) brushCopyDeferred = BrushGray;
            else brushCopyDeferred = brushDeferPath;

            Brush brushBackupDeferred;
            if (!backupDeferred) brushBackupDeferred = BrushLightGray;
            else brushBackupDeferred = brushDeferPath;

            // Apply brushes to boxes
            Box_LidarrURL.Background = brushLidarr;
            Box_BackupRootFolder.Background = brushBackup;
            Box_LidarrImport.Background = brushImport;
            Box_NotSelected.Background = brushNotSelected;
            Box_CopyImportedFiles.Background = brushCopy;
            Box_Defer.Background = brushDeferPath;
            var boxReleases = this.FindName("Box_LidarrReleases") as Border;
            if (boxReleases != null)
            {
                boxReleases.Background = brushImport;
            }

            // Apply arrow colors - use FindName for new elements
            var lineLidarrReleases = this.FindName("Line_LidarrURL_Releases") as Line;
            var lineReleasesImport = this.FindName("Line_Releases_Import") as Line;
            var lineReleasesNotSelected = this.FindName("Line_Releases_NotSelected") as Line;
            var lineReleasesDefer = this.FindName("Line_Releases_Defer") as Line;

            if (lineLidarrReleases != null) lineLidarrReleases.Stroke = MergeBrushes(brushLidarr, brushImport);
            if (lineReleasesImport != null) lineReleasesImport.Stroke = MergeBrushes(brushImport, brushImport);
            if (lineReleasesNotSelected != null) lineReleasesNotSelected.Stroke = MergeBrushes(brushImport, brushNotSelected);
            if (lineReleasesDefer != null) lineReleasesDefer.Stroke = MergeBrushes(brushImport, brushDeferPath);

            // Line_LidarrURL_Backup: now connects Import -> Backup (legacy name)
            var lineLidarrUrlBackup = this.FindName("Line_LidarrURL_Backup") as Line;
            if (lineLidarrUrlBackup != null) lineLidarrUrlBackup.Stroke = MergeBrushes(brushImport, brushBackup);

            var lineImportCopy = this.FindName("Line_Import_Copy") as Line;
            if (lineImportCopy != null) lineImportCopy.Stroke = !copyImported ? BrushGray : MergeBrushes(brushImport, brushCopy);
            var lineNotSelectedCopy = this.FindName("Line_NotSelected_Copy") as Line;
            if (lineNotSelectedCopy != null) lineNotSelectedCopy.Stroke = !copyNotSelected ? BrushGray : MergeBrushes(brushNotSelected, brushCopy);

            // NotSelected -> Backup
            var lineNotSelectedBackup = this.FindName("Line_NotSelected_Backup") as Line;
            if (lineNotSelectedBackup != null)
            {
                lineNotSelectedBackup.Stroke = !backupNotSelected ? BrushLightGray : MergeBrushes(brushNotSelected, brushBackup);
            }

            // Defer-related lines
            var lineDeferBackup = this.FindName("Line_Defer_Backup") as Line;
            if (lineDeferBackup != null) lineDeferBackup.Stroke = !backupDeferred ? BrushLightGray : MergeBrushes(brushDeferPath, brushBackupDeferred);
            var lineDeferCopy = this.FindName("Line_Defer_Copy") as Line;
            if (lineDeferCopy != null) lineDeferCopy.Stroke = !copyDeferred ? BrushGray : MergeBrushes(brushDeferPath, brushCopyDeferred);

            // Also set arrowhead fills (use FindName)
            var headLidarrReleases = this.FindName("Head_LidarrURL_Releases") as Polygon;
            if (headLidarrReleases != null && lineLidarrReleases != null) headLidarrReleases.Fill = lineLidarrReleases.Stroke;
            var headReleasesImport = this.FindName("Head_Releases_Import") as Polygon;
            if (headReleasesImport != null && lineReleasesImport != null) headReleasesImport.Fill = lineReleasesImport.Stroke;
            var headReleasesNotSelected = this.FindName("Head_Releases_NotSelected") as Polygon;
            if (headReleasesNotSelected != null && lineReleasesNotSelected != null) headReleasesNotSelected.Fill = lineReleasesNotSelected.Stroke;
            var headReleasesDefer = this.FindName("Head_Releases_Defer") as Polygon;
            if (headReleasesDefer != null && lineReleasesDefer != null) headReleasesDefer.Fill = lineReleasesDefer.Stroke;

            var headLidarrUrlBackup = this.FindName("Head_LidarrURL_Backup") as Polygon;
            if (headLidarrUrlBackup != null && lineLidarrUrlBackup != null) headLidarrUrlBackup.Fill = lineLidarrUrlBackup.Stroke;
            var headImportCopy = this.FindName("Head_Import_Copy") as Polygon;
            if (headImportCopy != null && lineImportCopy != null) headImportCopy.Fill = lineImportCopy.Stroke;
            var headNotSelectedCopy = this.FindName("Head_NotSelected_Copy") as Polygon;
            if (headNotSelectedCopy != null && lineNotSelectedCopy != null) headNotSelectedCopy.Fill = lineNotSelectedCopy.Stroke;

            var headNotSelectedBackup = this.FindName("Head_NotSelected_Backup") as Polygon;
            if (headNotSelectedBackup != null && lineNotSelectedBackup != null)
            {
                headNotSelectedBackup.Fill = lineNotSelectedBackup.Stroke;
            }

            var headDeferBackup = this.FindName("Head_Defer_Backup") as Polygon;
            if (headDeferBackup != null && lineDeferBackup != null) headDeferBackup.Fill = lineDeferBackup.Stroke;
            var headDeferCopy = this.FindName("Head_Defer_Copy") as Polygon;
            if (headDeferCopy != null && lineDeferCopy != null) headDeferCopy.Fill = lineDeferCopy.Stroke;

            // Ensure lines are positioned (in case layout already ready)
            PositionArrows();
        }

        private Brush MergeBrushes(Brush a, Brush b)
        {
            // If either is red -> red, else if either amber -> amber, else if either gray/lightgray -> gray, else green
            if (a == BrushRed || b == BrushRed) return BrushRed;
            if (a == BrushAmber || b == BrushAmber) return BrushAmber;
            if (a == BrushGray || b == BrushGray || a == BrushLightGray || b == BrushLightGray) return BrushGray;
            return BrushGreen;
        }

        private void PositionArrows()
        {
            // prevent re-entrancy
            if (_isPositioning) return;
            _isPositioning = true;

            try
            {
                if (ArrowsCanvas == null) return;

                // Helper to get center of a UIElement relative to the canvas
                Point Center(UIElement el)
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

                // Resolve box elements (use FindName for boxes that may have been renamed)
                var boxLidarr = this.FindName("Box_LidarrURL") as UIElement ?? Box_LidarrURL as UIElement;
                var boxReleases = this.FindName("Box_LidarrReleases") as UIElement;
                var boxImport = this.FindName("Box_LidarrImport") as UIElement ?? Box_LidarrImport as UIElement;
                var boxNotSel = this.FindName("Box_NotSelected") as UIElement ?? Box_NotSelected as UIElement;
                var boxDefer = this.FindName("Box_Defer") as UIElement ?? Box_Defer as UIElement;
                var boxBackup = this.FindName("Box_BackupRootFolder") as UIElement ?? Box_BackupRootFolder as UIElement;
                var boxCopy = this.FindName("Box_CopyImportedFiles") as UIElement ?? Box_CopyImportedFiles as UIElement;

                // Compute centers and rects (fall back to BoxesGrid when element missing)
                var defaultEl = BoxesGrid as UIElement;

                var elCenter = new System.Collections.Generic.Dictionary<string, Point>(StringComparer.OrdinalIgnoreCase);
                var elRect = new System.Collections.Generic.Dictionary<string, Rect>(StringComparer.OrdinalIgnoreCase);

                void AddElement(string key, UIElement? el)
                {
                    var actual = el ?? defaultEl;
                    elCenter[key] = Center(actual);
                    elRect[key] = GetElementRect(actual);
                }

                AddElement("Lidarr", boxLidarr);
                AddElement("Releases", boxReleases ?? defaultEl);
                AddElement("Import", boxImport);
                AddElement("NotSelected", boxNotSel);
                AddElement("Defer", boxDefer);
                AddElement("Backup", boxBackup);
                AddElement("Copy", boxCopy);

                // Helper to draw a named logical connection using elements map and UI elements
                void DrawConnection(string srcKey, string dstKey, string lineName, string headName)
                {
                    var line = this.FindName(lineName) as Line;
                    var head = this.FindName(headName) as Polygon;
                    if (line == null) return; // nothing to draw

                    var a = elCenter.ContainsKey(srcKey) ? elCenter[srcKey] : new Point(0, 0);
                    var b = elCenter.ContainsKey(dstKey) ? elCenter[dstKey] : new Point(0, 0);
                    var rSrc = elRect.ContainsKey(srcKey) ? elRect[srcKey] : Rect.Empty;
                    var rDst = elRect.ContainsKey(dstKey) ? elRect[dstKey] : Rect.Empty;

                    var start = GetRectIntersectionPoint(rSrc, a, b, preferNearA: true) ?? a;
                    var end = GetRectIntersectionPoint(rDst, a, b, preferNearA: false) ?? b;

                    SetLine(line, head, start, end);
                }

                // Now draw all logical connections in a consistent order
                // Lidarr URL -> Releases
                DrawConnection("Lidarr", "Releases", "Line_LidarrURL_Releases", "Head_LidarrURL_Releases");

                // Releases -> Import
                DrawConnection("Releases", "Import", "Line_Releases_Import", "Head_Releases_Import");

                // Releases -> NotSelected
                DrawConnection("Releases", "NotSelected", "Line_Releases_NotSelected", "Head_Releases_NotSelected");

                // Releases -> Defer
                DrawConnection("Releases", "Defer", "Line_Releases_Defer", "Head_Releases_Defer");

                // Import -> Backup (legacy line name kept)
                DrawConnection("Import", "Backup", "Line_LidarrURL_Backup", "Head_LidarrURL_Backup");

                // Import -> Copy
                DrawConnection("Import", "Copy", "Line_Import_Copy", "Head_Import_Copy");

                // NotSelected -> Copy
                DrawConnection("NotSelected", "Copy", "Line_NotSelected_Copy", "Head_NotSelected_Copy");

                // NotSelected -> Backup
                DrawConnection("NotSelected", "Backup", "Line_NotSelected_Backup", "Head_NotSelected_Backup");

                // Defer -> Backup
                DrawConnection("Defer", "Backup", "Line_Defer_Backup", "Head_Defer_Backup");

                // Defer -> Copy
                DrawConnection("Defer", "Copy", "Line_Defer_Copy", "Head_Defer_Copy");
            }
            finally
            {
                _isPositioning = false;
            }
        }

        private Rect GetElementRect(UIElement el)
        {
            try
            {
                if (el is FrameworkElement fe && ArrowsCanvas != null)
                {
                    var transform = fe.TransformToVisual(ArrowsCanvas);
                    var topLeft = transform.Transform(new Point(0,0));
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

            Point[] corners = new Point[] {
                new Point(rect.Left, rect.Top),
                new Point(rect.Right, rect.Top),
                new Point(rect.Right, rect.Bottom),
                new Point(rect.Left, rect.Bottom)
            };

            for (int i =0; i <4; i++)
            {
                var p1 = corners[i];
                var p2 = corners[(i +1) %4];
                var dx2 = p2.X - p1.X;
                var dy2 = p2.Y - p1.Y;

                var denom = dx1 * dy2 - dy1 * dx2;
                if (Math.Abs(denom) <1e-9) continue;

                var t = ((p1.X - a.X) * dy2 - (p1.Y - a.Y) * dx2) / denom;
                var u = ((p1.X - a.X) * dy1 - (p1.Y - a.Y) * dx1) / denom;

                // intersection must lie on the rectangle edge segment (u between0 and1)
                if (u >=0.0 -1e-9 && u <=1.0 +1e-9 && t >=0.0 -1e-9 && t <=1.0 +1e-9)
                {
                    var ix = a.X + t * dx1;
                    var iy = a.Y + t * dy1;
                    candidates.Add((new Point(ix, iy), t));
                }
            }

            if (candidates.Count ==0) return null;

            // choose intersection nearest to A or nearest to B depending on preferNearA
            if (preferNearA)
            {
                // smallest positive t
                double bestT = double.MaxValue;
                Point best = candidates[0].pt;
                foreach (var c in candidates)
                {
                    if (c.t >=0 && c.t < bestT)
                    {
                        bestT = c.t; best = c.pt;
                    }
                }
                // move a small amount outward so the line doesn't overlap the border
                return OffsetAlongLine(best, a, b,4.0);
            }
            else
            {
                // largest t <=1 (closest to B)
                double bestT = double.MinValue;
                Point best = candidates[0].pt;
                foreach (var c in candidates)
                {
                    if (c.t <=1 && c.t > bestT)
                    {
                        bestT = c.t; best = c.pt;
                    }
                }
                return OffsetAlongLine(best, a, b, -4.0);
            }
        }

        private Point OffsetAlongLine(Point pt, Point a, Point b, double offset)
        {
            // offset positive moves from pt towards b; negative moves towards a
            var vx = b.X - a.X;
            var vy = b.Y - a.Y;
            var len = Math.Sqrt(vx * vx + vy * vy);
            if (len <1e-6) return pt;
            var ux = vx / len;
            var uy = vy / len;
            return new Point(pt.X + ux * offset, pt.Y + uy * offset);
        }

        private void SetLine(Line line, Polygon head, Point a, Point b)
        {
            if (line == null) return;
            line.X1 = a.X;
            line.Y1 = a.Y;
            line.X2 = b.X;
            line.Y2 = b.Y;

            // compute arrowhead polygon (equilateral triangle) at b, oriented from a->b
            if (head == null) return;
            double angle = Math.Atan2(b.Y - a.Y, b.X - a.X);
            const double size =10.0;
            // base points relative to tip
            var p1 = new Point(b.X, b.Y);
            var p2 = new Point(b.X - size * Math.Cos(angle - Math.PI /6), b.Y - size * Math.Sin(angle - Math.PI /6));
            var p3 = new Point(b.X - size * Math.Cos(angle + Math.PI /6), b.Y - size * Math.Sin(angle + Math.PI /6));

            head.Points = new PointCollection() { p1, p2, p3 };
        }

        // Removed earlier duplicate mappings and handlers. Consolidated mappings and handlers are defined later in the file.

        private void HighlightSettings(SettingKey[]? keys, bool highlight)
        {
            // Reset all first
            foreach (var si in SettingsCollection)
            {
                si.IsHighlighted = false;
            }

            if (keys == null || !highlight) return;

            var keyNames = keys.Select(k => k.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var si in SettingsCollection)
            {
                if (keyNames.Contains(si.Name)) si.IsHighlighted = true;
            }
        }
    }
}