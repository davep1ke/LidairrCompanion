using System.Diagnostics;
using System.IO;
using System.Windows.Media;

namespace LidairrCompanion.Services
{
    // Simple audio play/pause helper using MediaPlayer. Runs on UI thread Dispatcher for WPF.
    public class AudioService : IDisposable
    {
        private readonly MediaPlayer _player = new MediaPlayer();
        private bool _isPlaying = false;

        public bool IsPlaying => _isPlaying;

        public AudioService()
        {
            // nothing
        }

        // Play a file with optional server->local mapping. Throws exceptions when file not found or invalid.
        public void PlayMapped(string filePath, string? serverImportPath, string? localMapping)
        {
            var resolved = ImportService.ResolveMappedPath(filePath, Models.SettingKey.LidarrImportPath, true);
            if (string.IsNullOrWhiteSpace(resolved)) throw new ArgumentNullException(nameof(filePath));
            if (!File.Exists(resolved)) throw new FileNotFoundException("Audio file not found", resolved);

            Stop();

            _player.Open(new Uri(resolved));
            _player.Play();
            _isPlaying = true;

            _player.MediaEnded += OnMediaEnded;
        }

        private void OnMediaEnded(object? sender, EventArgs e)
        {
            _isPlaying = false;
            // ensure position reset
            try { _player.Position = TimeSpan.Zero; } catch { }
        }

        public void Stop()
        {
            if (_isPlaying)
            {
                try { _player.Stop(); } catch { }
                _isPlaying = false;
                _player.MediaEnded -= OnMediaEnded;
            }
        }

        // Open the containing folder for a file path, resolving mapping if provided. Returns resolved folder path.
        public static string OpenContainingFolder(string filePath, string? serverImportPath, string? localMapping)
        {
            var resolved = ImportService.ResolveMappedPath(filePath, Models.SettingKey.LidarrImportPath, true);
            if (string.IsNullOrWhiteSpace(resolved)) throw new ArgumentNullException(nameof(filePath));

            var folder = resolved;
            if (File.Exists(resolved)) folder = Path.GetDirectoryName(resolved) ?? resolved;

            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                throw new DirectoryNotFoundException($"Folder not found: {folder}");

            // Launch explorer with selected file if possible
            try
            {
                // Use explorer with /select to highlight the file if file exists
                if (File.Exists(resolved))
                {
                    var arg = $"/select,\"{resolved}\"";
                    Process.Start(new ProcessStartInfo("explorer.exe", arg) { UseShellExecute = true });
                }
                else
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", folder) { UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                // wrap
                throw new InvalidOperationException("Failed to open folder", ex);
            }

            return folder;
        }

        public void Dispose()
        {
            try { Stop(); } catch { }
            _player.Close();
        }

        public void AdvanceBy(TimeSpan amount)
        {
            if (!_isPlaying) return;
            try
            {
                var pos = _player.Position;
                _player.Position = pos + amount;
            }
            catch
            {
                // ignore if cannot seek
            }
        }

        public TimeSpan? Position
        {
            get
            {
                try { return _player.Position; } catch { return null; }
            }
        }
    }
}
