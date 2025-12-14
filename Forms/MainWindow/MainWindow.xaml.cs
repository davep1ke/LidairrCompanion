using LidarrCompanion.Helpers;
using LidarrCompanion.Models;
using LidarrCompanion.Services;
using System.Collections.ObjectModel;
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
        public static readonly RoutedCommand CmdNotForImport = new RoutedCommand();
        public static readonly RoutedCommand CmdPlayTrack = new RoutedCommand();
        public static readonly RoutedCommand CmdDefer = new RoutedCommand();

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

        public MainWindow()
        {
            InitializeComponent();
            AppSettings.Load();

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
            CommandBindings.Add(new CommandBinding(CmdNotForImport, (s, e) => btn_NotForImport_Click(btn_NotForImport, new RoutedEventArgs())));
            CommandBindings.Add(new CommandBinding(CmdPlayTrack, (s, e) => btn_PlayTrack_Click(btn_PlayTrack, new RoutedEventArgs())));
            CommandBindings.Add(new CommandBinding(CmdDefer, (s, e) => btn_DeferImport_Click(null, new RoutedEventArgs())));

            // Add KeyBindings for Alt shortcuts
            this.InputBindings.Add(new KeyBinding(CmdGetNextFiles, Key.D1, ModifierKeys.Alt));
            this.InputBindings.Add(new KeyBinding(CmdGetArtists, Key.D2, ModifierKeys.Alt));
            this.InputBindings.Add(new KeyBinding(CmdAutoMatch, Key.D3, ModifierKeys.Alt));
            this.InputBindings.Add(new KeyBinding(CmdMatchArtist, Key.A, ModifierKeys.Alt));
            this.InputBindings.Add(new KeyBinding(CmdMarkMatch, Key.M, ModifierKeys.Alt));
            this.InputBindings.Add(new KeyBinding(CmdNotForImport, Key.N, ModifierKeys.Alt));
            this.InputBindings.Add(new KeyBinding(CmdPlayTrack, Key.P, ModifierKeys.Alt));
            this.InputBindings.Add(new KeyBinding(CmdDefer, Key.D, ModifierKeys.Alt));

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
                btn_DeferImport.IsEnabled = false;
                btn_autoReleaseMatchToArtist.IsEnabled = false;
                btn_manualReleaseMatchToArtist.IsEnabled = false;
                btn_MarkMatch.IsEnabled = false;
                btn_UnselectMatch.IsEnabled = false;
                btn_Settings.IsEnabled = false;
                btn_ClearProposed.IsEnabled = false;
                btn_NotForImport.IsEnabled = false;
                btn_UnlinkFile.IsEnabled = false;
                btn_DeleteFile.IsEnabled = false;
                
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
                btn_DeferImport.IsEnabled = true;
                btn_Import.IsEnabled = true;
                btn_autoReleaseMatchToArtist.IsEnabled = true;
                btn_manualReleaseMatchToArtist.IsEnabled = true;
                btn_MarkMatch.IsEnabled = true;
                btn_UnselectMatch.IsEnabled = true;
                btn_Settings.IsEnabled = true;
                btn_ClearProposed.IsEnabled = true;
                btn_NotForImport.IsEnabled = true;
                btn_UnlinkFile.IsEnabled = true;
                btn_DeleteFile.IsEnabled = true;
            });
        }

        private void btn_Settings_Click(object sender, RoutedEventArgs e)
        {
            var settingsform = new Settings();
            settingsform.ShowDialog();
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



    }
}