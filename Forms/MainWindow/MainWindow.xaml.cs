using LidarrCompanion.Helpers;
using LidarrCompanion.Models;
using LidarrCompanion.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace LidarrCompanion
{
    public partial class MainWindow : Window
    {
        // Commands for input bindings
        public static readonly RoutedCommand CmdGetNextFiles = new RoutedCommand();
        public static readonly RoutedCommand CmdGetArtists = new RoutedCommand();
        public static readonly RoutedCommand CmdAutoMatch = new RoutedCommand();
        public static readonly RoutedCommand CmdMatchArtist = new RoutedCommand();
        public static readonly RoutedCommand CmdMarkMatch = new RoutedCommand();
        public static readonly RoutedCommand CmdPlayTrack = new RoutedCommand();

        private ObservableCollection<LidarrManualImportFile> _manualImportFiles = new();
        private ObservableCollection<LidarrArtistReleaseTrack> _artistReleaseTracks = new();
        private ObservableCollection<ProposedAction> _proposedActions = new();
        
        // Keep quick lookup to prevent double assignment
        private HashSet<int> _assignedTrackIds = new();
        private HashSet<int> _assignedFileIds = new();

        // Busy state
        private bool _isBusy = false;

        // Audio player
        private FileAndAudioService? _audioPlayer;
        private ImportService _importService = new ImportService();
        private ProposalService _proposalService = new ProposalService();

        // Log window
        private LogWindow? _logWindow;

        // Sift window
        private SiftWindow? _siftWindow;

        public MainWindow()
        {
            InitializeComponent();
            AppSettings.Load();

            // Initialize log window
            _logWindow = new LogWindow();
            
            // Initialize sift window
            _siftWindow = new SiftWindow();
            
            // Log application startup
            Logger.Log("Application started", LogSeverity.Low);

            // _queueRecords and _artists are defined in the Unimported partial
            list_QueueRecords.ItemsSource = _queueRecords;
            list_Files_in_Release.ItemsSource = _manualImportFiles;
            list_Artist_Releases.ItemsSource = _artistReleaseTracks;
            list_Proposed_Actions.ItemsSource = _proposedActions;

            // Event handlers: list_QueueRecords.SelectionChanged is implemented in MainWindow.Unimported.cs
            list_QueueRecords.SelectionChanged += list_QueueRecords_SelectionChanged;
            list_Files_in_Release.SelectionChanged += list_Files_in_Release_SelectionChanged;

            // Hook command bindings to existing handlers (some handlers live in Unimported partial)
            CommandBindings.Add(new CommandBinding(CmdGetNextFiles, (s, e) => btn_GetFilesFromLidarr_Click(btn_GetFilesFromLidarr, new RoutedEventArgs())));
            CommandBindings.Add(new CommandBinding(CmdGetArtists, (s, e) => btn_GetArtists_Click(btn_GetArtists, new RoutedEventArgs())));
            CommandBindings.Add(new CommandBinding(CmdAutoMatch, (s, e) => btn_autoReleaseMatchToArtist_Click(btn_autoReleaseMatchToArtist, new RoutedEventArgs())));
            CommandBindings.Add(new CommandBinding(CmdMatchArtist, (s, e) => btn_manualReleaseMatchToArtist_Click(btn_manualReleaseMatchToArtist, new RoutedEventArgs())));
            CommandBindings.Add(new CommandBinding(CmdMarkMatch, (s, e) => btn_MarkMatch_Click(btn_MarkMatch, new RoutedEventArgs())));
            CommandBindings.Add(new CommandBinding(CmdPlayTrack, (s, e) => btn_PlayTrack_Click(btn_PlayTrack, new RoutedEventArgs())));

            // Add KeyBindings for Alt shortcuts
            this.InputBindings.Add(new KeyBinding(CmdGetNextFiles, Key.D1, ModifierKeys.Alt));
            this.InputBindings.Add(new KeyBinding(CmdGetArtists, Key.D2, ModifierKeys.Alt));
            this.InputBindings.Add(new KeyBinding(CmdAutoMatch, Key.D3, ModifierKeys.Alt));
            this.InputBindings.Add(new KeyBinding(CmdMatchArtist, Key.A, ModifierKeys.Alt));
            this.InputBindings.Add(new KeyBinding(CmdMarkMatch, Key.M, ModifierKeys.Alt));
            this.InputBindings.Add(new KeyBinding(CmdPlayTrack, Key.P, ModifierKeys.Alt));

            // Attach right-click event handler for play button
            btn_PlayTrack.MouseRightButtonUp += btn_PlayTrack_MouseRightButtonUp;

            // Hook into window closing event to clean up log window
            Closing += MainWindow_Closing;

            // Populate destination buttons
            Loaded += (s, e) => {
                PopulateDestinationButtons();
                
                // Restore window state from settings
                RestoreWindowState();
                
                Helpers.ThemeManager.ApplyTheme(this);
            };
        }

        private void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            // Save window state before closing
            SaveWindowState();
            
            // Allow log window to close when main window closes
            _logWindow?.AllowClose();
            _logWindow?.Close();
            
            // Allow sift window to close when main window closes
            _siftWindow?.AllowClose();
            _siftWindow?.Close();
        }

        private void RestoreWindowState()
        {
            try
            {
                // Restore maximized state
                var maximized = AppSettings.Current.GetTyped<bool>(SettingKey.WindowMaximized);
                if (maximized)
                {
                    WindowState = WindowState.Maximized;
                }
                else
                {
                    // Restore position and size
                    var left = AppSettings.GetValue(SettingKey.WindowLeft);
                    var top = AppSettings.GetValue(SettingKey.WindowTop);
                    var width = AppSettings.GetValue(SettingKey.WindowWidth);
                    var height = AppSettings.GetValue(SettingKey.WindowHeight);

                    if (double.TryParse(left, out var l) && l >= 0)
                        Left = l;
                    if (double.TryParse(top, out var t) && t >= 0)
                        Top = t;
                    if (double.TryParse(width, out var w) && w > 200)
                        Width = w;
                    if (double.TryParse(height, out var h) && h > 200)
                        Height = h;
                }

                // Subscribe to state/size/location changes
                StateChanged += (s, e) => SaveWindowState();
                SizeChanged += (s, e) => SaveWindowState();
                LocationChanged += (s, e) => SaveWindowState();
            }
            catch
            {
                // Ignore errors - use default window state
            }
        }

        private void SaveWindowState()
        {
            try
            {
                // Save maximized state
                AppSettings.Current.Settings[SettingKey.WindowMaximized.ToString()] = WindowState == WindowState.Maximized;

                // Only save position/size if not maximized
                if (WindowState == WindowState.Normal)
                {
                    AppSettings.Current.Settings[SettingKey.WindowLeft.ToString()] = Left.ToString();
                    AppSettings.Current.Settings[SettingKey.WindowTop.ToString()] = Top.ToString();
                    AppSettings.Current.Settings[SettingKey.WindowWidth.ToString()] = Width.ToString();
                    AppSettings.Current.Settings[SettingKey.WindowHeight.ToString()] = Height.ToString();
                }

                AppSettings.Save();
            }
            catch
            {
                // Ignore errors during state save
            }
        }

        private void SetBusy(string message)
        {
            _isBusy = true;
            Dispatcher.Invoke(() =>
            {
                txt_Status.Text = message;
                
                // Disable all buttons
                Helpers.ThemeManager.SetAllButtonsEnabled(this, false);

                // Disable and dim all ListViews
                list_QueueRecords.IsEnabled = false;
                list_QueueRecords.Opacity = 0.5;
                
                list_Files_in_Release.IsEnabled = false;
                list_Files_in_Release.Opacity = 0.5;
                
                list_Artist_Releases.IsEnabled = false;
                list_Artist_Releases.Opacity = 0.5;
                
                list_Proposed_Actions.IsEnabled = false;
                list_Proposed_Actions.Opacity = 0.5;
            });
        }

        private void ClearBusy()
        {
            _isBusy = false;
            Dispatcher.Invoke(() =>
            {
                txt_Status.Text = string.Empty;
                
                // Enable all buttons
                Helpers.ThemeManager.SetAllButtonsEnabled(this, true);

                // Re-enable and restore full opacity for all ListViews
                list_QueueRecords.IsEnabled = true;
                list_QueueRecords.Opacity = 1.0;
                
                list_Files_in_Release.IsEnabled = true;
                list_Files_in_Release.Opacity = 1.0;
                
                list_Artist_Releases.IsEnabled = true;
                list_Artist_Releases.Opacity = 1.0;
                
                list_Proposed_Actions.IsEnabled = true;
                list_Proposed_Actions.Opacity = 1.0;
            });
        }

        private void btn_Settings_Click(object sender, RoutedEventArgs e)
        {
            var settingsform = new Settings();
            settingsform.ShowDialog();
        }

        private void btn_ToggleDarkMode_Click(object sender, RoutedEventArgs e)
        {
            // Toggle the setting
            var currentValue = AppSettings.Current.GetTyped<bool>(SettingKey.DarkMode);
            var newValue = !currentValue;
            
            // Update setting
            AppSettings.Current.Settings[SettingKey.DarkMode.ToString()] = newValue;
            AppSettings.Save();
            
            // Apply the new theme
            Helpers.ThemeManager.ApplyTheme(this);
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

        private void btn_ToggleLogWindow_Click(object sender, RoutedEventArgs e)
        {
            if (_logWindow == null)
            {
                _logWindow = new LogWindow();
            }

            if (_logWindow.IsVisible)
            {
                _logWindow.Hide();
            }
            else
            {
                _logWindow.Show();
                _logWindow.Activate();
            }
        }

        private void btn_ToggleSiftWindow_Click(object sender, RoutedEventArgs e)
        {
            if (_siftWindow == null)
            {
                _siftWindow = new SiftWindow();
            }

            if (_siftWindow.IsVisible)
            {
                _siftWindow.Hide();
            }
            else
            {
                _siftWindow.Show();
                _siftWindow.Activate();
            }
        }
    }
}