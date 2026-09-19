namespace LidarrCompanion.Models
{
    public enum StatusKind { Idle, Info, Busy, Error }

    // Single home for the status/notice text that used to be scattered across the triage page
    // (a dismissible error banner, a separate "Artists: N" label, and an old-style status-bar
    // line all disagreeing with each other). Registered as a Singleton so TriageService and
    // SiftService (also Singletons, for the cross-scope reasons documented on TriageService) can
    // constructor-inject it directly - a Scoped registration here would hit the same captive-
    // dependency problem IJSRuntime does.
    //
    // Lives in Core (rather than next to the Blazor services that use it) purely so its expiry
    // behavior can be unit-tested; it has no web dependencies.
    public class StatusService
    {
        private int _version;

        public string? Message { get; private set; }
        public StatusKind Kind { get; private set; } = StatusKind.Idle;
        public bool IsBusy => Kind == StatusKind.Busy;

        // Info/Error notices clear themselves after this long. Busy is never auto-dismissed - it
        // is cleared by whichever operation set it. A property (not a constant) so tests can use a
        // tiny value instead of waiting minutes.
        public TimeSpan AutoDismissAfter { get; set; } = TimeSpan.FromMinutes(2);

        public event Action? Changed;

        public void SetBusy(string message) => Set(message, StatusKind.Busy);
        public void SetInfo(string message) => Set(message, StatusKind.Info);
        public void ShowError(string message) => Set(message, StatusKind.Error);

        private void Set(string message, StatusKind kind)
        {
            Message = message;
            Kind = kind;
            var version = Interlocked.Increment(ref _version);
            Changed?.Invoke();

            if (kind is StatusKind.Info or StatusKind.Error)
                _ = ExpireAsync(version);
        }

        // The version check is what stops an older notice's timer from wiping a newer message
        // that arrived in the meantime.
        private async Task ExpireAsync(int version)
        {
            await Task.Delay(AutoDismissAfter).ConfigureAwait(false);
            if (Volatile.Read(ref _version) == version && Kind is StatusKind.Info or StatusKind.Error)
                Dismiss();
        }

        // Called at the end of a busy operation - only clears if nothing (e.g. an error) set a
        // more specific status while the operation was running.
        public void ClearBusy()
        {
            if (Kind == StatusKind.Busy)
            {
                Message = null;
                Kind = StatusKind.Idle;
                Interlocked.Increment(ref _version);
                Changed?.Invoke();
            }
        }

        public void Dismiss()
        {
            Message = null;
            Kind = StatusKind.Idle;
            Interlocked.Increment(ref _version);
            Changed?.Invoke();
        }
    }
}
