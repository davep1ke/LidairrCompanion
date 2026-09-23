namespace LidarrCompanion.Helpers
{
    // Which action types still need their original source file to exist before processing.
    // Backing up (or re-validating) a file only makes sense for actions that haven't yet acted on
    // it - Import/Unlink/Delete/MoveToDestination all read or move the source. A VerifyImport
    // action is purely "ask Lidarr whether it landed" with no file I/O of its own: by the time one
    // exists, the source has already been handed to Lidarr (and may well be gone, moved into the
    // library) - re-validating it before a retry is not just redundant but actively wrong, and was
    // the root cause of a real bug where reprocessing a stuck-verification row aborted the entire
    // batch because its now-gone source file failed a pre-backup existence check.
    public static class ImportActionRules
    {
        public static bool RequiresSourceFile(ProposalActionType type) => type switch
        {
            ProposalActionType.Import => true,
            ProposalActionType.Unlink => true,
            ProposalActionType.Delete => true,
            ProposalActionType.MoveToDestination => true,
            ProposalActionType.VerifyImport => false,
            ProposalActionType.NotForImport => false,
            ProposalActionType.Defer => false,
            _ => false
        };
    }
}
