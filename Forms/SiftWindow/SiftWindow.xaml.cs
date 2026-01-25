using LidarrCompanion.Helpers;
using LidarrCompanion.Models;
using LidarrCompanion.Services;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace LidarrCompanion
{
    public partial class SiftWindow : Window
    {
        private bool _allowClose = false;
        private ObservableCollection<SiftTrack> _allTracks = new();
        private ObservableCollection<SiftTrack> _nextTracks = new();
        private SiftTrack? _currentTrack;
        private FileAndAudioService? _audioPlayer;
        private int _currentIndex = -1;
        private double _lastSeekPercent = 0.0;
        private DispatcherTimer? _progressTimer;
        private TimeSpan _trackDuration = TimeSpan.Zero;
        private int _defaultStartPosition = 0; // 0, 15, 30, 45, 60, 75, or 90

        public SiftWindow()
        {
            InitializeComponent();
            
            // Apply theme and load tracks after window is loaded
            Loaded += (s, e) => {
                Helpers.ThemeManager.ApplyTheme(this);
                LoadTracksFromFolder();
                LoadSettings();
                UpdateSeekButtonHighlights();
            };

            // Set up data binding
            list_NextTracks.ItemsSource = _nextTracks;

            // Set up keyboard shortcuts via KeyDown event
            this.KeyDown += SiftWindow_KeyDown;

            // Set up progress timer
            _progressTimer = new DispatcherTimer();
            _progressTimer.Interval = TimeSpan.FromMilliseconds(100);
            _progressTimer.Tick += ProgressTimer_Tick;
        }

        private void LoadSettings()
        {
            // Load volume setting
            var volume = AppSettings.Current.GetTyped<int>(SettingKey.SiftVolume);
            if (volume < 0) volume = 0;
            if (volume > 100) volume = 100;
            slider_Volume.Value = volume;
            
            // Load default position setting
            _defaultStartPosition = AppSettings.Current.GetTyped<int>(SettingKey.SiftDefaultPosition);
            
            // Validate default position is one of the allowed values
            int[] validPositions = { 0, 15, 30, 45, 60, 75, 90 };
            if (!validPositions.Contains(_defaultStartPosition))
            {
                _defaultStartPosition = 0;
            }
        }

        private void UpdateSeekButtonHighlights()
        {
            // Clear all highlights
            btn_Seek15.FontWeight = FontWeights.Normal;
            btn_Seek30.FontWeight = FontWeights.Normal;
            btn_Seek45.FontWeight = FontWeights.Normal;
            btn_Seek60.FontWeight = FontWeights.Normal;
            btn_Seek75.FontWeight = FontWeights.Normal;
            btn_Seek90.FontWeight = FontWeights.Normal;

            // Highlight the selected default position
            switch (_defaultStartPosition)
            {
                case 15:
                    btn_Seek15.FontWeight = FontWeights.Bold;
                    break;
                case 30:
                    btn_Seek30.FontWeight = FontWeights.Bold;
                    break;
                case 45:
                    btn_Seek45.FontWeight = FontWeights.Bold;
                    break;
                case 60:
                    btn_Seek60.FontWeight = FontWeights.Bold;
                    break;
                case 75:
                    btn_Seek75.FontWeight = FontWeights.Bold;
                    break;
                case 90:
                    btn_Seek90.FontWeight = FontWeights.Bold;
                    break;
            }
        }

        private void btn_SeekPosition_RightClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is Button button && button.Tag is string tagValue)
            {
                if (int.TryParse(tagValue, out int position))
                {
                    _defaultStartPosition = position;
                    
                    // Save to settings
                    AppSettings.Current.Settings[SettingKey.SiftDefaultPosition.ToString()] = position;
                    AppSettings.Save();
                    
                    // Update visual highlights
                    UpdateSeekButtonHighlights();
                    
                    txt_Status.Text = $"Default start position set to {position}%";
                    
                    e.Handled = true;
                }
            }
        }

        private void slider_Volume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            // Check if txt_Volume has been initialized yet (may not be during XAML loading)
            if (txt_Volume == null)
                return;
                
            var volume = (int)slider_Volume.Value;
            txt_Volume.Text = $"{volume}%";
            
            // Update audio player volume if playing
            if (_audioPlayer != null)
            {
                _audioPlayer.Volume = volume / 100.0;
            }
            
            // Save to settings
            AppSettings.Current.Settings[SettingKey.SiftVolume.ToString()] = volume;
            AppSettings.Save();
        }

        private void ProgressTimer_Tick(object? sender, EventArgs e)
        {
            if (_audioPlayer != null && _audioPlayer.IsPlaying)
            {
                var position = _audioPlayer.Position;
                if (position.HasValue && _trackDuration.TotalSeconds > 0)
                {
                    var percent = (position.Value.TotalSeconds / _trackDuration.TotalSeconds) * 100;
                    slider_Progress.Value = Math.Min(100, Math.Max(0, percent));
                    
                    // Update duration display with current position
                    txt_Duration.Text = $"{FormatTime(position.Value)} / {FormatTime(_trackDuration)}";
                }
            }
        }

        private string FormatTime(TimeSpan time)
        {
            if (time.TotalHours >= 1)
            {
                return $"{(int)time.TotalHours}:{time.Minutes:D2}:{time.Seconds:D2}";
            }
            return $"{time.Minutes}:{time.Seconds:D2}";
        }

        private void SiftWindow_KeyDown(object sender, KeyEventArgs e)
        {
            // Handle keyboard shortcuts
            switch (e.Key)
            {
                case Key.Space:
                    btn_Play_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                    break;
                case Key.Y:
                    btn_Keep_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                    break;
                case Key.N:
                    btn_Trash_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                    break;
                case Key.D1:
                case Key.NumPad1:
                    btn_Seek15_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                    break;
                case Key.D2:
                case Key.NumPad2:
                    btn_Seek30_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                    break;
                case Key.D3:
                case Key.NumPad3:
                    btn_Seek45_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                    break;
                case Key.D4:
                case Key.NumPad4:
                    btn_Seek60_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                    break;
                case Key.D5:
                case Key.NumPad5:
                    btn_Seek75_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                    break;
                case Key.D6:
                case Key.NumPad6:
                    btn_Seek90_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                    break;
                case Key.OemPlus:
                case Key.Add:
                    btn_SeekForward_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                    break;
                case Key.OemMinus:
                case Key.Subtract:
                    btn_SeekBackward_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                    break;
            }
        }

        private void LoadTracksFromFolder()
        {
            try
            {
                var siftFolder = AppSettings.GetValue(SettingKey.SiftFolder);
                if (string.IsNullOrWhiteSpace(siftFolder) || !Directory.Exists(siftFolder))
                {
                    txt_Status.Text = "Sift folder not configured or does not exist. Please check settings.";
                    return;
                }

                _allTracks.Clear();
                
                // Get all audio files from the folder
                var files = Directory.GetFiles(siftFolder, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(f => FileAndAudioService.AudioExtensions.Contains(
                        Path.GetExtension(f).ToLowerInvariant()))
                    .OrderBy(f => f)
                    .ToList();

                foreach (var file in files)
                {
                    var (artist, title, contributingArtists) = FileAndAudioService.ExtractMetadata(file);
                    
                    var track = new SiftTrack
                    {
                        FilePath = file,
                        FileName = Path.GetFileName(file),
                        CoverArt = FileAndAudioService.ExtractCoverArt(file),
                        Artist = artist,
                        Title = title,
                        ContributingArtists = contributingArtists
                    };
                    _allTracks.Add(track);
                }

                txt_Status.Text = $"Loaded {_allTracks.Count} tracks from {siftFolder}";
                
                if (_allTracks.Count > 0)
                {
                    LoadTrack(0);
                }
                else
                {
                    txt_Status.Text = "No audio files found in sift folder.";
                }
            }
            catch (Exception ex)
            {
                txt_Status.Text = $"Error loading tracks: {ex.Message}";
            }
        }

        private void LoadTrack(int index)
        {
            if (index < 0 || index >= _allTracks.Count)
                return;

            _currentIndex = index;
            _currentTrack = _allTracks[index];

            // Update UI - bind the entire track object to all relevant controls
            img_CoverArt.DataContext = _currentTrack;
            txt_CurrentFileName.DataContext = _currentTrack;
            txt_Artist.DataContext = _currentTrack;
            txt_Title.DataContext = _currentTrack;
            txt_ContributingArtists.DataContext = _currentTrack;

            // Get track duration
            try
            {
                using (var file = TagLib.File.Create(_currentTrack.FilePath))
                {
                    _trackDuration = file.Properties.Duration;
                    txt_Duration.Text = FormatTime(_trackDuration);
                }
            }
            catch
            {
                _trackDuration = TimeSpan.Zero;
                txt_Duration.Text = "0:00";
            }

            // Reset progress
            slider_Progress.Value = 0;

            // Update next tracks list
            UpdateNextTracksList();

            // Stop any existing playback
            StopPlayback();
        }

        private void UpdateNextTracksList()
        {
            _nextTracks.Clear();
            
            int nextIndex = _currentIndex + 1;
            for (int i = 0; i < 10 && nextIndex < _allTracks.Count; i++, nextIndex++)
            {
                _nextTracks.Add(_allTracks[nextIndex]);
            }
        }

        private void btn_Play_Click(object sender, RoutedEventArgs e)
        {
            if (_currentTrack == null)
                return;

            try
            {
                if (_audioPlayer != null && _audioPlayer.IsPlaying)
                {
                    StopPlayback();
                    btn_Play.Content = "Play (Space)";
                    _progressTimer?.Stop();
                }
                else
                {
                    //// Check if file format is supported
                    //var extension = Path.GetExtension(_currentTrack.FilePath).ToLowerInvariant();
                    //if (extension == ".opus")
                    //{
                    //    txt_Status.Text = "Opus files are not supported. Please install codec pack or convert to another format.";
                    //    MessageBox.Show("Opus audio format is not natively supported by Windows Media Player.\n\n" +
                    //                  "To play Opus files, you can:\n" +
                    //                  "1. Install a codec pack (e.g., K-Lite, LAV Filters)\n" +
                    //                  "2. Convert the file to FLAC, MP3, or another supported format",
                    //                  "Unsupported Format", MessageBoxButton.OK, MessageBoxImage.Information);
                    //    return;
                    //}
                    
                    _audioPlayer?.Dispose();
                    _audioPlayer = new FileAndAudioService();
                    
                    // Set volume from slider
                    _audioPlayer.Volume = slider_Volume.Value / 100.0;
                    
                    _audioPlayer.PlayMapped(_currentTrack.FilePath, null, null);
                    
                    // Use last seek percent if set, otherwise use default start position
                    var startPercent = _lastSeekPercent > 0 ? _lastSeekPercent : _defaultStartPosition / 100.0;
                    if (startPercent > 0)
                    {
                        SeekToPercent(startPercent);
                    }
                    
                    _currentTrack.IsPlaying = true;
                    btn_Play.Content = "Stop (Space)";
                    _progressTimer?.Start();
                }
            }
            catch (Exception ex)
            {
                var extension = Path.GetExtension(_currentTrack?.FilePath ?? "").ToLowerInvariant();
                if (extension == ".opus" || extension == ".ogg")
                {
                    txt_Status.Text = $"Unsupported audio format: {extension}. Install codec pack or convert file.";
                }
                else
                {
                    txt_Status.Text = $"Playback error: {ex.Message}";
                }
            }
        }

        private void btn_Keep_Click(object sender, RoutedEventArgs e)
        {
            if (_currentTrack == null)
                return;

            try
            {
                var backupEnabled = AppSettings.Current.GetTyped<bool>(SettingKey.BackupFilesBeforeImport);
                var backupRoot = AppSettings.GetValue(SettingKey.BackupRootFolder);
                var importPath = AppSettings.GetValue(SettingKey.LidarrImportPathLocal);

                if (string.IsNullOrWhiteSpace(importPath))
                {
                    txt_Status.Text = "Import path not configured.";
                    return;
                }

                if (!Directory.Exists(importPath))
                {
                    Directory.CreateDirectory(importPath);
                }

                // Backup if enabled
                if (backupEnabled && !string.IsNullOrWhiteSpace(backupRoot))
                {
                    if (!Directory.Exists(backupRoot))
                    {
                        Directory.CreateDirectory(backupRoot);
                    }
                    
                    var backupFile = Path.Combine(backupRoot, _currentTrack.FileName);
                    File.Copy(_currentTrack.FilePath, backupFile, true);
                }

                // Move to import folder
                var destinationFile = Path.Combine(importPath, _currentTrack.FileName);
                File.Move(_currentTrack.FilePath, destinationFile);

                txt_Status.Text = $"Kept: {_currentTrack.FileName}";
                
                // Remove from list and load next
                _allTracks.RemoveAt(_currentIndex);
                LoadNextTrack();
            }
            catch (Exception ex)
            {
                txt_Status.Text = $"Error keeping file: {ex.Message}";
            }
        }

        private void btn_Trash_Click(object sender, RoutedEventArgs e)
        {
            if (_currentTrack == null)
                return;

            try
            {
                var backupEnabled = AppSettings.Current.GetTyped<bool>(SettingKey.BackupFilesBeforeImport);
                var backupRoot = AppSettings.GetValue(SettingKey.BackupRootFolder);

                // Backup if enabled
                if (backupEnabled && !string.IsNullOrWhiteSpace(backupRoot))
                {
                    if (!Directory.Exists(backupRoot))
                    {
                        Directory.CreateDirectory(backupRoot);
                    }
                    
                    var backupFile = Path.Combine(backupRoot, _currentTrack.FileName);
                    File.Copy(_currentTrack.FilePath, backupFile, true);
                }

                // Delete the file
                File.Delete(_currentTrack.FilePath);

                txt_Status.Text = $"Trashed: {_currentTrack.FileName}";
                
                // Remove from list and load next
                _allTracks.RemoveAt(_currentIndex);
                LoadNextTrack();
            }
            catch (Exception ex)
            {
                txt_Status.Text = $"Error trashing file: {ex.Message}";
            }
        }

        private void LoadNextTrack()
        {
            bool wasPlaying = _audioPlayer != null && _audioPlayer.IsPlaying;
            
            if (_currentIndex < _allTracks.Count)
            {
                LoadTrack(_currentIndex);
                
                // Auto-play next track if previous was playing
                // Use default start position instead of last seek percent
                if (wasPlaying && _defaultStartPosition > 0)
                {
                    _lastSeekPercent = _defaultStartPosition / 100.0;
                    btn_Play_Click(btn_Play, new RoutedEventArgs());
                }
                else if (wasPlaying)
                {
                    btn_Play_Click(btn_Play, new RoutedEventArgs());
                }
            }
            else
            {
                txt_Status.Text = "No more tracks to sift.";
                _currentTrack = null;
                img_CoverArt.DataContext = null;
                txt_CurrentFileName.DataContext = null;
                txt_Artist.DataContext = null;
                txt_Title.DataContext = null;
                txt_ContributingArtists.DataContext = null;
            }
        }

        private void SeekToPercent(double percent)
        {
            if (_audioPlayer == null || !_audioPlayer.IsPlaying)
                return;

            try
            {
                var position = _audioPlayer.Position;
                if (!position.HasValue)
                    return;

                // Get the file duration using TagLib
                using (var file = TagLib.File.Create(_currentTrack!.FilePath))
                {
                    var duration = file.Properties.Duration;
                    var seekTime = TimeSpan.FromMilliseconds(duration.TotalMilliseconds * percent);
                    
                    // Calculate the difference and advance
                    var diff = seekTime - position.Value;
                    _audioPlayer.AdvanceBy(diff);
                }
            }
            catch
            {
                // Ignore seek errors
            }
        }

        private void btn_SeekBackward_Click(object sender, RoutedEventArgs e)
        {
            if (_audioPlayer != null && _audioPlayer.IsPlaying)
            {
                var position = _audioPlayer.Position;
                if (position.HasValue)
                {
                    _audioPlayer.AdvanceBy(TimeSpan.FromSeconds(-position.Value.TotalSeconds * 0.05));
                }
            }
        }

        private void btn_SeekForward_Click(object sender, RoutedEventArgs e)
        {
            if (_audioPlayer != null && _audioPlayer.IsPlaying)
            {
                var position = _audioPlayer.Position;
                if (position.HasValue)
                {
                    _audioPlayer.AdvanceBy(TimeSpan.FromSeconds(position.Value.TotalSeconds * 0.05));
                }
            }
        }

        private void btn_Seek15_Click(object sender, RoutedEventArgs e)
        {
            _lastSeekPercent = 0.15;
            if (_audioPlayer != null && _audioPlayer.IsPlaying)
            {
                SeekToPercent(0.15);
            }
            else
            {
                btn_Play_Click(sender, e);
            }
        }

        private void btn_Seek30_Click(object sender, RoutedEventArgs e)
        {
            _lastSeekPercent = 0.30;
            if (_audioPlayer != null && _audioPlayer.IsPlaying)
            {
                SeekToPercent(0.30);
            }
            else
            {
                btn_Play_Click(sender, e);
            }
        }

        private void btn_Seek45_Click(object sender, RoutedEventArgs e)
        {
            _lastSeekPercent = 0.45;
            if (_audioPlayer != null && _audioPlayer.IsPlaying)
            {
                SeekToPercent(0.45);
            }
            else
            {
                btn_Play_Click(sender, e);
            }
        }

        private void btn_Seek60_Click(object sender, RoutedEventArgs e)
        {
            _lastSeekPercent = 0.60;
            if (_audioPlayer != null && _audioPlayer.IsPlaying)
            {
                SeekToPercent(0.60);
            }
            else
            {
                btn_Play_Click(sender, e);
            }
        }

        private void btn_Seek75_Click(object sender, RoutedEventArgs e)
        {
            _lastSeekPercent = 0.75;
            if (_audioPlayer != null && _audioPlayer.IsPlaying)
            {
                SeekToPercent(0.75);
            }
            else
            {
                btn_Play_Click(sender, e);
            }
        }

        private void btn_Seek90_Click(object sender, RoutedEventArgs e)
        {
            _lastSeekPercent = 0.90;
            if (_audioPlayer != null && _audioPlayer.IsPlaying)
            {
                SeekToPercent(0.90);
            }
            else
            {
                btn_Play_Click(sender, e);
            }
        }

        private void StopPlayback()
        {
            _progressTimer?.Stop();
            
            if (_audioPlayer != null)
            {
                _audioPlayer.Stop();
                _audioPlayer.Dispose();
                _audioPlayer = null;
            }

            if (_currentTrack != null)
            {
                _currentTrack.IsPlaying = false;
            }
            
            slider_Progress.Value = 0;
        }

        public void AllowClose()
        {
            _allowClose = true;
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            _progressTimer?.Stop();
            StopPlayback();
            
            if (!_allowClose)
            {
                e.Cancel = true;
                Hide();
            }
        }
    }
}
