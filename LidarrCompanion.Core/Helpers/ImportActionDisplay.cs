namespace LidarrCompanion.Helpers
{
    // Pure display text for a ProposedAction's current status, computed from state rather than
    // stored redundantly alongside it (Status/RetryCount/MaxRetries/ErrorMessage are the source of
    // truth; nothing has to remember to keep a separate label string in sync with them).
    public static class ImportActionDisplay
    {
        public static string Describe(ProposedAction action) => action.Status switch
        {
            ImportActionStatus.Pending => "Pending",
            ImportActionStatus.Sent => "Sent to Lidarr",
            ImportActionStatus.Verifying => action.MaxRetries > 0
                ? $"Verifying ({action.RetryCount}/{action.MaxRetries})..."
                : "Verifying...",
            ImportActionStatus.Success => "Success",
            ImportActionStatus.Failed => string.IsNullOrWhiteSpace(action.ErrorMessage) ? "Failed" : action.ErrorMessage,
            _ => action.Status.ToString()
        };
    }
}
