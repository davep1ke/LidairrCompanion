using LidarrCompanion.Helpers;
using LidarrCompanion.Models;
using System.Text.RegularExpressions;

namespace LidarrCompanion.Web.Services
{
    // Ported from Helpers/ThemeManager.cs's GetActionBackground/GetActionForeground/GetThemedColor
    // - same color-selection logic (including the dark-mode-aware settings lookups and the
    // per-destination color override for MoveToDestination), just returning hex strings for
    // inline CSS style attributes instead of WPF Brush objects.
    public static class ActionColorHelper
    {
        public static string GetActionBackground(ProposalActionType action, string? destinationName = null)
        {
            var darkMode = AppSettings.Current.GetTyped<bool>(SettingKey.DarkMode);

            if (action == ProposalActionType.MoveToDestination && !string.IsNullOrWhiteSpace(destinationName))
            {
                var dest = AppSettings.Current.ImportDestinations?.FirstOrDefault(d => d.Name == destinationName);
                if (dest != null)
                {
                    var colorKey = darkMode ? dest.ColorDark : dest.Color;
                    return IsValidHex(colorKey) ? colorKey! : "#87CEEB";
                }
                return "#87CEEB";
            }

            return action switch
            {
                ProposalActionType.Import => GetSetting(darkMode ? SettingKey.ColorImportMatchDark : SettingKey.ColorImportMatch, "#90EE90"),
                ProposalActionType.NotForImport => GetSetting(darkMode ? SettingKey.ColorNotForImportDark : SettingKey.ColorNotForImport, "#FFA500"),
                ProposalActionType.Defer => GetSetting(darkMode ? SettingKey.ColorDeferDark : SettingKey.ColorDefer, "#FAFAD2"),
                ProposalActionType.Unlink => GetSetting(darkMode ? SettingKey.ColorUnlinkDark : SettingKey.ColorUnlink, "#FFA07A"),
                ProposalActionType.Delete => GetSetting(darkMode ? SettingKey.ColorDeleteDark : SettingKey.ColorDelete, "#F08080"),
                ProposalActionType.MoveToDestination => "#87CEEB",
                _ => "transparent"
            };
        }

        public static string GetActionForeground(ProposalActionType action, string? destinationName = null)
        {
            var bg = GetActionBackground(action, destinationName);
            return IsDarkColor(bg) ? "#fff" : "#000";
        }

        public static string GetThemedColor(string colorName)
        {
            var darkMode = AppSettings.Current.GetTyped<bool>(SettingKey.DarkMode);
            return colorName.ToLowerInvariant() switch
            {
                "trackhasfile" => GetSetting(darkMode ? SettingKey.ColorTrackHasFileDark : SettingKey.ColorTrackHasFile, "#D3D3D3"),
                "releasehasassigned" => GetSetting(darkMode ? SettingKey.ColorReleaseHasAssignedDark : SettingKey.ColorReleaseHasAssigned, "#ADD8E6"),
                "trackassigned" => GetSetting(darkMode ? SettingKey.ColorTrackAssignedDark : SettingKey.ColorTrackAssigned, "#90EE90"),
                _ => "transparent"
            };
        }

        public static string GetQueueMatchColor(ReleaseMatchType match) => match switch
        {
            ReleaseMatchType.Exact => "#008000",
            ReleaseMatchType.ArtistFirst => "#90EE90",
            ReleaseMatchType.AlbumFirst => "#98FB98",
            ReleaseMatchType.Partial => "#0000FF",
            _ => "transparent"
        };

        // Exact/Partial get an explicit light-on-dark background above, so they need white text
        // in either theme. ArtistFirst/AlbumFirst use light green backgrounds, so black text
        // still reads fine either way. Everything else has no background (transparent, inheriting
        // the page's own background) - forcing black there produced unreadable black-on-black
        // rows once the page itself could be dark, so that case inherits the page's text color
        // instead of hardcoding one.
        public static string GetQueueMatchForeground(ReleaseMatchType match) => match switch
        {
            ReleaseMatchType.Exact or ReleaseMatchType.Partial => "#fff",
            ReleaseMatchType.ArtistFirst or ReleaseMatchType.AlbumFirst => "#000",
            _ => "inherit"
        };

        private static string GetSetting(SettingKey key, string fallback)
        {
            var v = AppSettings.GetValue(key);
            return IsValidHex(v) ? v : fallback;
        }

        private static bool IsValidHex(string? v) => !string.IsNullOrWhiteSpace(v) && Regex.IsMatch(v, "^#[0-9A-Fa-f]{6}$");

        private static bool IsDarkColor(string hex)
        {
            if (!IsValidHex(hex)) return false;
            var r = Convert.ToInt32(hex.Substring(1, 2), 16);
            var g = Convert.ToInt32(hex.Substring(3, 2), 16);
            var b = Convert.ToInt32(hex.Substring(5, 2), 16);
            double luminance = (0.299 * r + 0.587 * g + 0.114 * b) / 255;
            return luminance < 0.5;
        }
    }
}
