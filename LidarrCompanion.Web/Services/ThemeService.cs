using LidarrCompanion.Models;

namespace LidarrCompanion.Web.Services
{
    // Tiny singleton so the settings page can push a live theme change out immediately (via a JS
    // call toggling <html data-theme>) without a full page reload. App.razor reads AppSettings
    // directly for the *initial* render (no flash-of-wrong-theme on first load); this only covers
    // updating already-open pages when the setting changes mid-session - matching the app's
    // single-active-session assumption, same as StatusService/TriageService.
    public class ThemeService
    {
        public bool IsDarkMode { get; private set; } = AppSettings.Current.GetTyped<bool>(SettingKey.DarkMode);

        public event Action? Changed;

        // Called after Settings.razor commits any settings change - cheap to just re-sync rather
        // than tracking whether DarkMode specifically was the field that changed.
        public void SyncFromSettings()
        {
            var current = AppSettings.Current.GetTyped<bool>(SettingKey.DarkMode);
            if (current == IsDarkMode) return;

            IsDarkMode = current;
            Changed?.Invoke();
        }
    }
}
