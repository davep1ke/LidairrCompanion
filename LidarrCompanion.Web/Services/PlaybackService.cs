namespace LidarrCompanion.Web.Services
{
    // What the persistent player bar needs to know about the current track. StreamUrl points at
    // a streaming endpoint (/audio/stream/{id} for the triage screen, /audio/sift-stream/{id}
    // for Sift). StartPercent seeks there once playback begins (used when Sift auto-advances to
    // the next track after Keep/Trash while something was already playing).
    public record NowPlayingTrack(string StreamUrl, string Title, string? Artist, string? CoverArtUrl, double StartPercent = 0);

    // Single shared playback state for the whole app. The main triage screen's track preview and
    // Sift both call into this same service instead of each owning their own player, which is
    // what let the old WPF app's two separate MediaPlayer instances play over each other. The
    // actual <audio> element and JS interop live in the PlayerBar component (rendered once, in
    // MainLayout, outside @Body) - this service just holds the state every page/component agrees
    // on and reacts to.
    //
    // Position/Duration/Volume are inherently client-side now (the <audio> element owns them,
    // same as any browser media player) rather than server-side like WPF's MediaPlayer.Position -
    // PlayerBar reports them back here via JS interop (timeupdate/loadedmetadata events) so pages
    // like Sift can read/react to them without owning any audio/JS themselves.
    public interface IPlaybackService
    {
        NowPlayingTrack? Current { get; }
        bool IsPlaying { get; }
        TimeSpan Position { get; }
        TimeSpan Duration { get; }
        double Volume { get; }

        event Action? StateChanged;

        // PlayerBar is what actually owns the <audio> element, so it's the one that turns these
        // into JS calls.
        event Action<TimeSpan>? RelativeSeekRequested;
        event Action<double>? PercentSeekRequested;
        event Action<double>? VolumeChangeRequested;

        void Play(NowPlayingTrack track);
        void Stop();
        void AdvanceBy(TimeSpan amount);
        void SeekToPercent(double percent);
        void SetVolume(double volume);

        // Called from JS interop once the <audio> element actually reports state.
        void SetPlaying(bool isPlaying);
        void SetProgress(TimeSpan position, TimeSpan duration);
    }

    public sealed class PlaybackService : IPlaybackService
    {
        public NowPlayingTrack? Current { get; private set; }
        public bool IsPlaying { get; private set; }
        public TimeSpan Position { get; private set; }
        public TimeSpan Duration { get; private set; }
        public double Volume { get; private set; } = 0.5;

        public event Action? StateChanged;
        public event Action<TimeSpan>? RelativeSeekRequested;
        public event Action<double>? PercentSeekRequested;
        public event Action<double>? VolumeChangeRequested;

        public void Play(NowPlayingTrack track)
        {
            Current = track;
            IsPlaying = true;
            Position = TimeSpan.Zero;
            Duration = TimeSpan.Zero;
            StateChanged?.Invoke();
        }

        public void Stop()
        {
            Current = null;
            IsPlaying = false;
            Position = TimeSpan.Zero;
            Duration = TimeSpan.Zero;
            StateChanged?.Invoke();
        }

        public void AdvanceBy(TimeSpan amount) => RelativeSeekRequested?.Invoke(amount);

        public void SeekToPercent(double percent) => PercentSeekRequested?.Invoke(percent);

        public void SetVolume(double volume)
        {
            Volume = Math.Clamp(volume, 0.0, 1.0);
            VolumeChangeRequested?.Invoke(Volume);
            StateChanged?.Invoke();
        }

        public void SetPlaying(bool isPlaying)
        {
            IsPlaying = isPlaying;
            StateChanged?.Invoke();
        }

        public void SetProgress(TimeSpan position, TimeSpan duration)
        {
            Position = position;
            Duration = duration;
            StateChanged?.Invoke();
        }
    }
}
