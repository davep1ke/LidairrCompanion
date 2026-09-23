namespace LidarrCompanion.Helpers
{
    // Replaces the old free-form ImportStatus string ("", "Success", "Failed", ad-hoc
    // "Post-Import Copy (3/30)" progress text all mixed into one field, compared by string
    // equality throughout). Sent/Verifying only apply to Import actions that go through the
    // async post-import verification step; every other action type goes straight from Pending to
    // Success/Failed synchronously.
    public enum ImportActionStatus
    {
        Pending,
        Sent,
        Verifying,
        Success,
        Failed
    }
}
