using LidairrCompanion.Models;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace LidairrCompanion
{
    public partial class Settings : Window
    {
        public ObservableCollection<SettingItem> SettingsCollection { get; set; }

        // Brushes
        private readonly Brush BrushGreen = new SolidColorBrush(Color.FromRgb(76, 175, 80));
        private readonly Brush BrushAmber = new SolidColorBrush(Color.FromRgb(255, 193, 7));
        private readonly Brush BrushRed = new SolidColorBrush(Color.FromRgb(244, 67, 54));
        private readonly Brush BrushLightGray = new SolidColorBrush(Color.FromRgb(211, 211, 211));
        private readonly Brush BrushGray = new SolidColorBrush(Color.FromRgb(169, 169, 169));

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

            // Apply brushes to boxes
            Box_LidarrURL.Background = brushLidarr;
            Box_BackupRootFolder.Background = brushBackup;
            Box_LidarrImportPath.Background = brushImport;
            Box_NotSelectedPath.Background = brushNotSelected;
            Box_CopyImportedFilesPath.Background = brushCopy;

            // Apply arrow colors
            // Line_LidarrURL_Backup: light gray if backups disabled
            Line_LidarrURL_Backup.Stroke = !backupEnabled ? BrushLightGray : MergeBrushes(brushLidarr, brushBackup);
            Line_LidarrURL_Import.Stroke = MergeBrushes(brushLidarr, brushImport);
            Line_Import_NotSelected.Stroke = MergeBrushes(brushImport, brushNotSelected);
            Line_LidarrURL_Copy.Stroke = !copyImported ? BrushGray : MergeBrushes(brushLidarr, brushCopy);
            Line_Import_Copy.Stroke = !copyImported ? BrushGray : MergeBrushes(brushImport, brushCopy);
            Line_NotSelected_Copy.Stroke = !copyImported ? BrushGray : MergeBrushes(brushNotSelected, brushCopy);

            // Also set arrowhead fills
            Head_LidarrURL_Backup.Fill = Line_LidarrURL_Backup.Stroke;
            Head_LidarrURL_Import.Fill = Line_LidarrURL_Import.Stroke;
            Head_Import_NotSelected.Fill = Line_Import_NotSelected.Stroke;
            Head_LidarrURL_Copy.Fill = Line_LidarrURL_Copy.Stroke;
            Head_Import_Copy.Fill = Line_Import_Copy.Stroke;
            Head_NotSelected_Copy.Fill = Line_NotSelected_Copy.Stroke;

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
                        // Use TransformToVisual because ArrowsCanvas is a sibling, not an ancestor
                        var transform = el.TransformToVisual(ArrowsCanvas);
                        var topLeft = transform.Transform(new Point(0, 0));
                        if (el is FrameworkElement fe)
                        {
                            return new Point(topLeft.X + fe.ActualWidth / 2, topLeft.Y + fe.ActualHeight / 2);
                        }
                        return topLeft;
                    }
                    catch
                    {
                        return new Point(0, 0);
                    }
                }

                var pLidarr = Center(Box_LidarrURL);
                var pBackup = Center(Box_BackupRootFolder);
                var pImport = Center(Box_LidarrImportPath);
                var pNotSel = Center(Box_NotSelectedPath);
                var pCopy = Center(Box_CopyImportedFilesPath);

                SetLine(Line_LidarrURL_Backup, Head_LidarrURL_Backup, pLidarr, pBackup);
                SetLine(Line_LidarrURL_Import, Head_LidarrURL_Import, pLidarr, pImport);
                SetLine(Line_Import_NotSelected, Head_Import_NotSelected, pImport, pNotSel);
                SetLine(Line_LidarrURL_Copy, Head_LidarrURL_Copy, pLidarr, pCopy);
                SetLine(Line_Import_Copy, Head_Import_Copy, pImport, pCopy);
                SetLine(Line_NotSelected_Copy, Head_NotSelected_Copy, pNotSel, pCopy);
            }
            finally
            {
                _isPositioning = false;
            }
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
            const double size = 10.0;
            // base points relative to tip
            var p1 = new Point(b.X, b.Y);
            var p2 = new Point(b.X - size * Math.Cos(angle - Math.PI / 6), b.Y - size * Math.Sin(angle - Math.PI / 6));
            var p3 = new Point(b.X - size * Math.Cos(angle + Math.PI / 6), b.Y - size * Math.Sin(angle + Math.PI / 6));

            head.Points = new PointCollection() { p1, p2, p3 };
        }
    }
}