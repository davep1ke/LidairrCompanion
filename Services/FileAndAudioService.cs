using LidarrCompanion.Helpers;
using LidarrCompanion.Models;
using LidarrCompanion.Services;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LidarrCompanion.Services
{
    // Combined file and audio helper: audio playback plus file/path helpers used across the app.
    public class FileAndAudioService : IDisposable
    {
        private readonly MediaPlayer _player = new MediaPlayer();
        private bool _isPlaying = false;
        private bool _isDisposed = false;
        private readonly object _lockObject = new object();

        public bool IsPlaying
        {
            get
            {
                lock (_lockObject)
                {
                    return _isPlaying && !_isDisposed;
                }
            }
        }

        public double Volume
        {
            get
            {
                if (_isDisposed) return 0;
                try { return _player.Volume; }
                catch (System.Runtime.InteropServices.SEHException) { return 0; }
                catch { return 0; }
            }
            set
            {
                if (_isDisposed) return;
                try { _player.Volume = Math.Max(0, Math.Min(1, value)); }
                catch (System.Runtime.InteropServices.SEHException sehEx)
                {
                    Logger.Log($"SEH exception setting volume: {sehEx.Message}", LogSeverity.Low, new { Error = sehEx.Message });
                }
                catch { /* Ignore volume setting errors */ }
            }
        }

        public FileAndAudioService()
        {
            // nothing
        }

        // Play a file with optional server->local mapping. Throws exceptions when file not found or invalid.
        public void PlayMapped(string filePath, string? serverImportPath, string? localMapping)
        {
            lock (_lockObject)
            {
                if (_isDisposed)
                    throw new ObjectDisposedException(nameof(FileAndAudioService));

                var resolved = FileOperationsHelper.ResolveMappedPath(filePath, SettingKey.LidarrImportPath, true);
                if (string.IsNullOrWhiteSpace(resolved)) throw new ArgumentNullException(nameof(filePath));
                if (!File.Exists(resolved)) throw new FileNotFoundException("Audio file not found", resolved);

                Stop();

                try
                {
                    _player.Open(new Uri(resolved));
                    _player.Play();
                    _isPlaying = true;
                    _player.MediaEnded += OnMediaEnded;
                }
                catch (System.Runtime.InteropServices.SEHException sehEx)
                {
                    _isPlaying = false;
                    Logger.Log($"SEH exception during audio playback start: {sehEx.Message}", LogSeverity.Medium, new { Error = sehEx.Message, FilePath = resolved });
                    throw new InvalidOperationException($"Failed to play audio file due to a native component error. The file may use an unsupported codec or be corrupted: {Path.GetFileName(resolved)}", sehEx);
                }
                catch (Exception ex)
                {
                    _isPlaying = false;
                    Logger.Log($"Exception during audio playback start: {ex.Message}", LogSeverity.Low, new { Error = ex.Message, FilePath = resolved });
                    throw;
                }
            }
        }

        private void OnMediaEnded(object? sender, EventArgs e)
        {
            lock (_lockObject)
            {
                if (_isDisposed) return;

                _isPlaying = false;
                
                // ensure position reset
                try
                {
                    _player.Position = TimeSpan.Zero;
                }
                catch (System.Runtime.InteropServices.SEHException sehEx)
                {
                    Logger.Log($"SEH exception resetting position on media end: {sehEx.Message}", LogSeverity.Low, new { Error = sehEx.Message });
                }
                catch { /* Ignore position reset errors */ }
            }
        }

        public void Stop()
        {
            lock (_lockObject)
            {
                if (_isDisposed) return;

                if (_isPlaying)
                {
                    // Detach event handler first to prevent callbacks during stop
                    try
                    {
                        _player.MediaEnded -= OnMediaEnded;
                    }
                    catch { /* Ignore event detach errors */ }

                    // Stop playback
                    try
                    {
                        _player.Stop();
                    }
                    catch (System.Runtime.InteropServices.SEHException sehEx)
                    {
                        Logger.Log($"SEH exception during stop: {sehEx.Message}", LogSeverity.Low, new { Error = sehEx.Message });
                    }
                    catch { /* Ignore stop errors */ }

                    _isPlaying = false;
                }
            }
        }

        // Open the containing folder for a file path, resolving mapping if provided. Returns resolved folder path.
        public static string OpenContainingFolder(string filePath, string? serverImportPath, string? localMapping)
        {
            var resolved = FileOperationsHelper.ResolveMappedPath(filePath, SettingKey.LidarrImportPath, true);
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
            lock (_lockObject)
            {
                if (_isDisposed) return;
                _isDisposed = true;

                // Stop playback first
                try
                {
                    if (_isPlaying)
                    {
                        _player.MediaEnded -= OnMediaEnded;
                        _player.Stop();
                        _isPlaying = false;
                    }
                }
                catch (System.Runtime.InteropServices.SEHException sehEx)
                {
                    Logger.Log($"SEH exception during dispose stop: {sehEx.Message}", LogSeverity.Low, new { Error = sehEx.Message });
                }
                catch { /* Ignore stop errors during disposal */ }

                // Close the player
                try
                {
                    _player.Close();
                }
                catch (System.Runtime.InteropServices.SEHException sehEx)
                {
                    Logger.Log($"SEH exception during player close: {sehEx.Message}", LogSeverity.Low, new { Error = sehEx.Message });
                }
                catch { /* Ignore close errors during disposal */ }
            }
        }

        public void AdvanceBy(TimeSpan amount)
        {
            lock (_lockObject)
            {
                if (_isDisposed || !_isPlaying) return;

                try
                {
                    var pos = _player.Position;
                    _player.Position = pos + amount;
                }
                catch (System.Runtime.InteropServices.SEHException sehEx)
                {
                    Logger.Log($"SEH exception during seek: {sehEx.Message}", LogSeverity.Low, new { Error = sehEx.Message });
                }
                catch
                {
                    // ignore if cannot seek
                }
            }
        }

        public TimeSpan? Position
        {
            get
            {
                if (_isDisposed) return null;
                
                try
                {
                    return _player.Position;
                }
                catch (System.Runtime.InteropServices.SEHException)
                {
                    return null;
                }
                catch
                {
                    return null;
                }
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

        // Common audio file extensions
        // Note: .opus files may not play without additional codecs installed
        public static readonly string[] AudioExtensions = new[]
        {
            ".mp3", ".flac", ".m4a", ".aac", ".ogg", ".opus",
            ".wma", ".wav", ".ape", ".wv", ".tta", ".mpc"
        };

        // Extract cover art from an audio file. Returns null if no cover art found.
        public static ImageSource? ExtractCoverArt(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return null;

            try
            {
                using (var file = TagLib.File.Create(filePath))
                {
                    var pictures = file.Tag.Pictures;
                    if (pictures == null || pictures.Length == 0)
                        return null;

                    // Get the first picture (usually the front cover)
                    var picture = pictures[0];
                    if (picture.Data == null || picture.Data.Count == 0)
                        return null;

                    using (var ms = new System.IO.MemoryStream(picture.Data.Data))
                    {
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.StreamSource = ms;
                        bitmap.EndInit();
                        bitmap.Freeze();
                        return bitmap;
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        // Extract metadata (artist, title, contributing artists) from an audio file
        public static (string artist, string title, string contributingArtists) ExtractMetadata(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return (string.Empty, string.Empty, string.Empty);

            try
            {
                using (var file = TagLib.File.Create(filePath))
                {
                    var artist = file.Tag.FirstPerformer ?? file.Tag.FirstAlbumArtist ?? string.Empty;
                    var title = file.Tag.Title ?? string.Empty;
                    
                    // Get contributing artists (all performers except the first one)
                    var performers = file.Tag.Performers ?? Array.Empty<string>();
                    var contributingArtists = string.Empty;
                    
                    if (performers.Length > 1)
                    {
                        // Join all performers except the first one
                        contributingArtists = string.Join(", ", performers.Skip(1));
                    }
                    else if (performers.Length == 0 && file.Tag.AlbumArtists != null && file.Tag.AlbumArtists.Length > 0)
                    {
                        // If no performers but album artists exist, use those
                        contributingArtists = string.Join(", ", file.Tag.AlbumArtists.Skip(1));
                    }

                    return (artist, title, contributingArtists);
                }
            }
            catch
            {
                return (string.Empty, string.Empty, string.Empty);
            }
        }

        /// <summary>
        /// Save cover art to an audio file
        /// </summary>
        /// <param name="filePath">Path to the audio file</param>
        /// <param name="imageData">Image data as byte array</param>
        /// <returns>True if successful, false otherwise</returns>
        public static bool SaveCoverArt(string filePath, byte[] imageData)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return false;

            if (imageData == null || imageData.Length == 0)
                return false;

            try
            {
                using (var file = TagLib.File.Create(filePath))
                {
                    // Create picture
                    var picture = new TagLib.Picture(imageData)
                    {
                        Type = TagLib.PictureType.FrontCover,
                        Description = "Cover"
                    };

                    // Set the picture
                    file.Tag.Pictures = new TagLib.IPicture[] { picture };
                    file.Save();

                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Save cover art to an audio file from a BitmapSource
        /// </summary>
        /// <param name="filePath">Path to the audio file</param>
        /// <param name="image">BitmapSource image</param>
        /// <returns>True if successful, false otherwise</returns>
        public static bool SaveCoverArt(string filePath, System.Windows.Media.Imaging.BitmapSource image)
        {
            if (image == null)
                return false;

            try
            {
                // Convert BitmapSource to byte array
                var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 95 };
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));

                using (var ms = new MemoryStream())
                {
                    encoder.Save(ms);
                    return SaveCoverArt(filePath, ms.ToArray());
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
