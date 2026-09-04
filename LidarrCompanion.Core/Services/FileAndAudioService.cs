using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace LidarrCompanion.Services
{
    // Portable subset of the WPF app's FileAndAudioService: the pure string/path helpers that
    // MatchingService depends on, plus cover-art/metadata extraction. Playback (WPF MediaPlayer)
    // is Windows-only and is NOT ported here - the Web project streams audio directly via an
    // HTTP endpoint instead. Cover-art/metadata extraction, unlike the WPF version, needs no
    // Windows-only pieces at all: TagLibSharp itself is already cross-platform, the WPF app's
    // ExtractCoverArt/ExtractMetadata just also converted the result into a WPF BitmapImage,
    // which isn't needed here - callers get raw bytes + mime type and serve them directly.
    public static class FileAndAudioService
    {
        // Extract cover art as raw bytes ready to serve over HTTP. Returns null if the file has
        // no embedded picture.
        public static (byte[] Data, string MimeType)? ExtractCoverArt(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return null;

            try
            {
                using var file = TagLib.File.Create(filePath);
                var picture = file.Tag.Pictures?.FirstOrDefault();
                if (picture?.Data is null || picture.Data.Count == 0)
                    return null;

                var mimeType = string.IsNullOrWhiteSpace(picture.MimeType) ? "image/jpeg" : picture.MimeType;
                return (picture.Data.Data, mimeType);
            }
            catch
            {
                return null;
            }
        }

        // Embed cover art into an audio file, replacing any existing pictures. Returns false on
        // any failure (missing file, invalid image data, tag write error) rather than throwing.
        public static bool SaveCoverArt(string filePath, byte[] imageData)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return false;

            if (imageData is null || imageData.Length == 0)
                return false;

            try
            {
                using var file = TagLib.File.Create(filePath);
                var picture = new TagLib.Picture(imageData)
                {
                    Type = TagLib.PictureType.FrontCover,
                    Description = "Cover"
                };
                file.Tag.Pictures = new TagLib.IPicture[] { picture };
                file.Save();
                return true;
            }
            catch
            {
                return false;
            }
        }

        // Extract metadata (artist, title, album, contributing artists) from an audio file.
        public static (string artist, string title, string album, string contributingArtists) ExtractMetadata(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return (string.Empty, string.Empty, string.Empty, string.Empty);

            try
            {
                using var file = TagLib.File.Create(filePath);
                var artist = file.Tag.FirstPerformer ?? file.Tag.FirstAlbumArtist ?? string.Empty;
                var title = file.Tag.Title ?? string.Empty;
                var album = file.Tag.Album ?? string.Empty;

                var performers = file.Tag.Performers ?? Array.Empty<string>();
                var contributingArtists = string.Empty;

                if (performers.Length > 1)
                {
                    contributingArtists = string.Join(", ", performers.Skip(1));
                }
                else if (performers.Length == 0 && file.Tag.AlbumArtists is { Length: > 0 })
                {
                    contributingArtists = string.Join(", ", file.Tag.AlbumArtists.Skip(1));
                }

                return (artist, title, album, contributingArtists);
            }
            catch
            {
                return (string.Empty, string.Empty, string.Empty, string.Empty);
            }
        }

        // Common audio file extensions
        // Note: .opus files may not play without additional codecs installed
        public static readonly string[] AudioExtensions = new[]
        {
            ".mp3", ".flac", ".m4a", ".aac", ".ogg", ".opus",
            ".wma", ".wav", ".ape", ".wv", ".tta", ".mpc"
        };

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
