using LidarrCompanion.Helpers;
using LidarrCompanion.Models;
using LidarrCompanion.Services;

namespace LidarrCompanion.Web.Services
{
    public class SiftTrack
    {
        public required int Id { get; init; }
        public required string FilePath { get; init; }
        public required string FileName { get; init; }
        public string Artist { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public string ContributingArtists { get; init; } = string.Empty;
        public bool HasCoverArt { get; init; }
    }

    // Ported from Forms/SiftWindow/SiftWindow.xaml.cs. Registered as a Singleton for the same
    // reason as TriageService: the /audio/sift-stream/{id} and /audio/sift-cover/{id} endpoints
    // run in their own per-request DI scope and need to resolve the same instance the Sift page
    // is using, which only a Singleton registration guarantees.
    //
    // Position/duration/playback itself is not this service's concern at all - that's
    // IPlaybackService/PlayerBar, shared with the main triage screen's track preview (this is
    // exactly the fix for the old WPF app's two separate MediaPlayer instances stepping on each
    // other). This service only owns the track list and the Keep/Trash file operations.
    public class SiftService
    {
        private static readonly int[] ValidPositions = { 0, 15, 30, 45, 60, 75, 90 };

        private readonly StatusService _status;
        private readonly List<SiftTrack> _allTracks = new();
        private int _nextId;

        public IReadOnlyList<SiftTrack> AllTracks => _allTracks;
        public IReadOnlyList<SiftTrack> NextTracks { get; private set; } = Array.Empty<SiftTrack>();
        public SiftTrack? CurrentTrack { get; private set; }
        public bool HasAttemptedLoad { get; private set; }

        public int DefaultStartPositionPercent { get; private set; }
        public double LastSeekPercent { get; set; }

        public SiftService(StatusService status)
        {
            _status = status;
            DefaultStartPositionPercent = LoadDefaultStartPosition();
        }

        private static int LoadDefaultStartPosition()
        {
            var value = AppSettings.Current.GetTyped<int>(SettingKey.SiftDefaultPosition);
            return Array.IndexOf(ValidPositions, value) >= 0 ? value : 0;
        }

        public void SetDefaultStartPosition(int percent)
        {
            if (Array.IndexOf(ValidPositions, percent) < 0) return;
            DefaultStartPositionPercent = percent;
            AppSettings.Current.Settings[SettingKey.SiftDefaultPosition.ToString()] = percent;
            AppSettings.Save();
            _status.SetInfo($"Default start position set to {percent}%");
        }

        public void LoadTracksFromFolder()
        {
            HasAttemptedLoad = true;
            var siftFolder = AppSettings.GetValue(SettingKey.SiftFolder);
            if (string.IsNullOrWhiteSpace(siftFolder) || !Directory.Exists(siftFolder))
            {
                _status.ShowError("Sift folder not configured or does not exist. Please check settings.");
                return;
            }

            _allTracks.Clear();
            _nextId = 0;

            try
            {
                var files = Directory.GetFiles(siftFolder, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(f => FileAndAudioService.AudioExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    .OrderBy(f => f)
                    .ToList();

                foreach (var file in files)
                {
                    var (artist, title, _, contributingArtists) = FileAndAudioService.ExtractMetadata(file);
                    var hasCoverArt = FileAndAudioService.ExtractCoverArt(file) is not null;

                    _allTracks.Add(new SiftTrack
                    {
                        Id = _nextId++,
                        FilePath = file,
                        FileName = Path.GetFileName(file),
                        Artist = artist,
                        Title = title,
                        ContributingArtists = contributingArtists,
                        HasCoverArt = hasCoverArt
                    });
                }

                if (_allTracks.Count > 0)
                    _status.SetInfo($"Loaded {_allTracks.Count} tracks from {siftFolder}");
                else
                    _status.ShowError("No audio files found in sift folder.");

                StartAtRandomLetter();
            }
            catch (Exception ex)
            {
                _status.ShowError($"Error loading tracks: {ex.Message}");
            }
        }

        private readonly Random _random = new();

        // Keeps the queue alphabetical but starts it at a random letter (wrapping to A after Z), so
        // the end of the alphabet gets its turn even when the queue is rarely emptied. Called on load
        // and again each time the Sift page is opened.
        public void StartAtRandomLetter()
        {
            if (_allTracks.Count == 0)
            {
                LoadTrack(0);
                return;
            }

            var letter = AlphabeticalRotation.PickStartLetter(_allTracks, t => t.FileName, _random);
            var ordered = letter is { } l
                ? AlphabeticalRotation.StartingAt(_allTracks, t => t.FileName, l)
                : _allTracks.OrderBy(t => t.FileName, StringComparer.OrdinalIgnoreCase).ToList();

            _allTracks.Clear();
            _allTracks.AddRange(ordered);
            LoadTrack(0);
        }

        private void LoadTrack(int index)
        {
            if (index < 0 || index >= _allTracks.Count)
            {
                CurrentTrack = null;
                NextTracks = Array.Empty<SiftTrack>();
                return;
            }

            CurrentTrack = _allTracks[index];
            UpdateNextTracksList(index);
        }

        private void UpdateNextTracksList(int currentIndex)
        {
            NextTracks = _allTracks.Skip(currentIndex + 1).Take(10).ToList();
        }

        public TimeSpan GetDuration(SiftTrack track)
        {
            try
            {
                using var file = TagLib.File.Create(track.FilePath);
                return file.Properties.Duration;
            }
            catch
            {
                return TimeSpan.Zero;
            }
        }

        // Returns (wasLastTrack) so the caller (Sift page) knows whether to keep playing the
        // next track or show "no more tracks".
        public bool Keep()
        {
            if (CurrentTrack is not { } track) return false;

            try
            {
                var backupEnabled = AppSettings.Current.GetTyped<bool>(SettingKey.BackupFilesBeforeImport);
                var backupRoot = AppSettings.GetValue(SettingKey.BackupRootFolder);
                var importPath = AppSettings.GetValue(SettingKey.ImportPathCompanion);

                if (string.IsNullOrWhiteSpace(importPath))
                {
                    _status.ShowError("Import path not configured.");
                    return false;
                }

                Directory.CreateDirectory(importPath);

                if (backupEnabled && !string.IsNullOrWhiteSpace(backupRoot))
                {
                    Directory.CreateDirectory(backupRoot);
                    File.Copy(track.FilePath, Path.Combine(backupRoot, track.FileName), true);
                }

                var destinationFile = Path.Combine(importPath, track.FileName);
                File.Move(track.FilePath, destinationFile);

                _status.SetInfo($"Kept: {track.FileName}");
                return AdvanceAfterRemoval(track);
            }
            catch (Exception ex)
            {
                _status.ShowError($"Error keeping file: {ex.Message}");
                return false;
            }
        }

        public bool Trash()
        {
            if (CurrentTrack is not { } track) return false;

            try
            {
                var backupEnabled = AppSettings.Current.GetTyped<bool>(SettingKey.BackupFilesBeforeImport);
                var backupRoot = AppSettings.GetValue(SettingKey.BackupRootFolder);

                if (backupEnabled && !string.IsNullOrWhiteSpace(backupRoot))
                {
                    Directory.CreateDirectory(backupRoot);
                    File.Copy(track.FilePath, Path.Combine(backupRoot, track.FileName), true);
                }

                File.Delete(track.FilePath);

                _status.SetInfo($"Trashed: {track.FileName}");
                return AdvanceAfterRemoval(track);
            }
            catch (Exception ex)
            {
                _status.ShowError($"Error trashing file: {ex.Message}");
                return false;
            }
        }

        private bool AdvanceAfterRemoval(SiftTrack removed)
        {
            var index = _allTracks.IndexOf(removed);
            if (index >= 0) _allTracks.RemoveAt(index);

            if (index < _allTracks.Count)
            {
                LoadTrack(index);
                return true;
            }

            _status.SetInfo("No more tracks to sift.");
            CurrentTrack = null;
            NextTracks = Array.Empty<SiftTrack>();
            return false;
        }
    }
}
