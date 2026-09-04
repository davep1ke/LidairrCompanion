namespace LidarrCompanion.Web.Services
{
    public enum StatusKind { Idle, Info, Busy, Error }

    // Single home for the status/notice text that used to be scattered across the triage page
    // (a dismissible error banner, a separate "Artists: N" label, and an old-style status-bar
    // line all disagreeing with each other). Registered as a Singleton so TriageService and
    // SiftService (also Singletons, for the cross-scope reasons documented on TriageService) can
    // constructor-inject it directly - a Scoped registration here would hit the same captive-
    // dependency problem IJSRuntime does.
    public class StatusService
    {
        public string? Message { get; private set; }
        public StatusKind Kind { get; private set; } = StatusKind.Idle;
        public bool IsBusy => Kind == StatusKind.Busy;

        public event Action? Changed;

        public void SetBusy(string message) => Set(message, StatusKind.Busy);
        public void SetInfo(string message) => Set(message, StatusKind.Info);
        public void ShowError(string message) => Set(message, StatusKind.Error);

        private void Set(string message, StatusKind kind)
        {
            Message = message;
            Kind = kind;
            Changed?.Invoke();
        }

        // Called at the end of a busy operation - only clears if nothing (e.g. an error) set a
        // more specific status while the operation was running.
        public void ClearBusy()
        {
            if (Kind == StatusKind.Busy)
            {
                Message = null;
                Kind = StatusKind.Idle;
                Changed?.Invoke();
            }
        }

        public void Dismiss()
        {
            Message = null;
            Kind = StatusKind.Idle;
            Changed?.Invoke();
        }
    }
}
