using LidarrCompanion.Helpers;
using LidarrCompanion.Services;

namespace LidarrCompanion.Web.Services
{
    // One item in the cover-art gate: a file whose destination requires artwork, ported from the
    // WPF app's CoverArtItem. CoverArtPreview is raw bytes here (served as a data URI) instead of
    // a WPF ImageSource.
    public class CoverArtQueueItem
    {
        public ProposedAction? Action { get; set; }
        public string FileName { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public string ReleaseName { get; set; } = string.Empty;
        public string DestinationName { get; set; } = string.Empty;
        public string? Artist { get; set; }
        public string? Album { get; set; }
        public bool HasCoverArt { get; set; }
        public byte[]? CoverArtPreview { get; set; }
        public string? CoverArtMimeType { get; set; }
    }

    // Holds the import pipeline's "pending cover art" pause state. Registered as a singleton
    // (like TriageService) so the triage page that parks a gate and the /cover-art page that
    // resolves it - potentially different browser tabs/circuits - see the same state.
    //
    // Unlike the original design sketch (a TaskCompletionSource awaited mid-pipeline), this holds
    // only passive state: ImportRunner.PrepareImport hands back the items that still need art,
    // TriageService stashes them here and returns immediately instead of blocking the triage page
    // (which would sit behind pointer-events:none for as long as the user takes to resolve them -
    // a real trap for a human-timescale wait in Blazor Server). Resolving the gate (Complete or
    // Abort) is a separate, explicit call back into TriageService to resume or cancel the rest of
    // the pipeline.
    public class CoverArtGateService
    {
        private List<ProposedAction>? _actionsSnapshot;

        public List<CoverArtQueueItem> PendingItems { get; } = new();
        public bool HasPending => PendingItems.Count > 0;
        public int MissingCount => PendingItems.Count(i => !i.HasCoverArt);

        public event Action? Changed;

        public void BeginGate(List<CoverArtQueueItem> items, List<ProposedAction> actionsSnapshot)
        {
            PendingItems.Clear();
            PendingItems.AddRange(items);
            _actionsSnapshot = actionsSnapshot;
            NotifyChanged();
        }

        public void SaveCoverArt(CoverArtQueueItem item, byte[] imageData, string mimeType)
        {
            if (!FileAndAudioService.TrySaveCoverArt(item.FilePath, imageData, out var error))
                throw new InvalidOperationException($"Failed to save cover art to '{item.FilePath}': {error}.");

            item.HasCoverArt = true;
            item.CoverArtPreview = imageData;
            item.CoverArtMimeType = mimeType;
            NotifyChanged();
        }

        // Called when the user clicks Complete. Hands back the stashed actions snapshot so the
        // caller can resume the rest of the import pipeline, and clears the gate.
        public List<ProposedAction>? CompleteAndTakeActions()
        {
            var snapshot = _actionsSnapshot;
            Clear();
            return snapshot;
        }

        // Called when the user clicks Abort. Clears the gate without returning the actions - the
        // proposed actions remain untouched, same as the WPF app's abort behavior.
        public void Abort()
        {
            Clear();
        }

        private void Clear()
        {
            PendingItems.Clear();
            _actionsSnapshot = null;
            NotifyChanged();
        }

        private void NotifyChanged() => Changed?.Invoke();
    }
}
