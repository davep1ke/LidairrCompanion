// Applies the dark/light theme to the current tab immediately after the DarkMode setting
// changes, without a full page reload. The *initial* render's data-theme attribute is set
// server-side by App.razor (reading AppSettings directly), so there's no flash-of-wrong-theme on
// first load - this only handles live updates while the page is already open.
export function apply(isDark) {
    document.documentElement.setAttribute('data-theme', isDark ? 'dark' : 'light');
}
