using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System;
using System.Text.RegularExpressions;

namespace LidarrCompanion.Services
{
    // Combined file and audio helper: audio playback plus file/path helpers used across the app.
    public class FileAndAudioService : IDisposable
    {
        private readonly MediaPlayer _player = new MediaPlayer();
        private bool _isPlaying = false;

        public bool IsPlaying => _isPlaying;

        public FileAndAudioService()
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

            // Normalize separators and get full path to avoid mixed slash issues
            try
            {
                resolved = resolved.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
                resolved = Path.GetFullPath(resolved);
            }
            catch
            {
                // If Path.GetFullPath fails, fall back to the normalized path
                try { resolved = resolved.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar); } catch { }
            }

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
                    // Build argument as: /select,"<fullpath>" — avoid injecting extra escaping
                    var arg = "/select," + "\"" + resolved + "\"";
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

        // Get the last folder segment from a path. If the path is a file, return its parent folder name.
        public static string? GetLowestFolderName(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            // Trim trailing separators
            path = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // If path looks like a file (has extension), return the parent folder's name
            if (Path.HasExtension(path))
            {
                var parent = Path.GetDirectoryName(path);
                if (string.IsNullOrWhiteSpace(parent))
                    return Path.GetFileName(path); // fallback
                return Path.GetFileName(parent);
            }

            // Otherwise return the last segment
            return Path.GetFileName(path);
        }

        // Normalise strings for comparison: lower-case, remove punctuation, collapse whitespace
        public static string Normalize(string s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return string.Empty;

            var lowered = s.ToLowerInvariant();
            lowered = Regex.Replace(lowered, @"[.,;:!\""'()\[\]\/\\\-_]+", " ");
            lowered = Regex.Replace(lowered, @"\s+", " ").Trim();
            return lowered;
        }

        // Normalize path for simple equality checks: unify separators and trim trailing separators.
        public static string NormalizePathForComparison(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            var p = path.Trim();
            p = p.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            p = p.TrimEnd(Path.DirectorySeparatorChar);
            return p;
        }

        // Heuristic: determine whether this release should be treated as a single file.
        // Rule:
        // - If configured importPath is empty -> false
        // - If outputPath equals importPath (after normalizing) -> single file
        // - If outputPath is a file and its parent directory equals importPath -> single file
        public static bool IsSingleFileRelease(string? outputPath, string? importPath)
        {
            if (string.IsNullOrWhiteSpace(importPath) || string.IsNullOrWhiteSpace(outputPath))
                return false;

            try
            {
                var normImport = NormalizePathForComparison(importPath);
                var normOutput = NormalizePathForComparison(outputPath);

                if (string.Equals(normOutput, normImport, StringComparison.OrdinalIgnoreCase))
                    return true;

                if (Path.HasExtension(outputPath))
                {
                    var parent = Path.GetDirectoryName(outputPath);
                    if (!string.IsNullOrWhiteSpace(parent))
                    {
                        var normParent = NormalizePathForComparison(parent);
                        if (string.Equals(normParent, normImport, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
            }
            catch
            {
                // If any path parsing fails, fall back to treating as folder (safer).
            }

            return false;
        }
    }
}
