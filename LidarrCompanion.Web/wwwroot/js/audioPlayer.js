// Minimal wrapper around the single <audio> element that lives in MainLayout (see PlayerBar).
// Actual decode/seek/playback is entirely the browser's native <audio> - this just starts/stops/
// seeks it and reports state changes (play/pause/ended/position/duration) back to
// IPlaybackService via dotNetRef, so any page's C# state stays in sync with what's actually
// happening client-side without owning any audio/JS itself.

export function play(audioEl, url, dotNetRef, startPercent) {
    if (!audioEl) return;
    audioEl.src = url;

    if (startPercent && startPercent > 0) {
        const onLoaded = () => {
            if (isFinite(audioEl.duration)) {
                audioEl.currentTime = audioEl.duration * startPercent;
            }
            audioEl.removeEventListener('loadedmetadata', onLoaded);
        };
        audioEl.addEventListener('loadedmetadata', onLoaded);
    }

    audioEl.play().catch(() => {
        // Autoplay can be blocked before any user gesture on the page; the user pressing
        // Play/Alt+P/Space counts as a gesture in every browser that matters here, so this is
        // mostly a safety net against transient errors, not an expected path.
    });
    attachHandlers(audioEl, dotNetRef);
}

export function stop(audioEl) {
    if (!audioEl) return;
    audioEl.pause();
    audioEl.removeAttribute('src');
    audioEl.load();
}

export function togglePlayPause(audioEl) {
    if (!audioEl) return;
    if (audioEl.paused) audioEl.play().catch(() => {});
    else audioEl.pause();
}

// The seek bar's value is driven from here (on every timeupdate tick, same as OnProgress) rather
// than from a Blazor re-render bound to Playback.Position - re-rendering a <input type="range">
// on every progress tick would fight the user's own drag. Dragging it instead seeks the audio
// element directly, client-side, with no server round-trip needed for the scrub itself; the
// existing timeupdate -> OnProgress reporting picks up the new position afterward as normal.
export function registerSeekBar(audioEl, seekBarEl) {
    if (!audioEl || !seekBarEl) return;

    let dragging = false;
    seekBarEl.addEventListener('pointerdown', () => { dragging = true; });
    seekBarEl.addEventListener('pointerup', () => { dragging = false; });
    seekBarEl.addEventListener('input', () => {
        if (isFinite(audioEl.duration) && audioEl.duration > 0) {
            audioEl.currentTime = (seekBarEl.value / 100) * audioEl.duration;
        }
    });

    audioEl._lcSeekBar = seekBarEl;
    audioEl._lcSeekBarDragging = () => dragging;
}

export function advanceBy(audioEl, seconds) {
    if (!audioEl || !isFinite(audioEl.duration)) return;
    audioEl.currentTime = Math.max(0, Math.min(audioEl.duration, audioEl.currentTime + seconds));
}

export function seekToPercent(audioEl, percent) {
    if (!audioEl || !isFinite(audioEl.duration)) return;
    audioEl.currentTime = audioEl.duration * percent;
}

export function setVolume(audioEl, volume) {
    if (!audioEl) return;
    audioEl.volume = Math.max(0, Math.min(1, volume));
}

let handlersAttached = new WeakSet();

function attachHandlers(audioEl, dotNetRef) {
    if (handlersAttached.has(audioEl)) return;
    handlersAttached.add(audioEl);

    audioEl.addEventListener('play', () => dotNetRef.invokeMethodAsync('OnAudioStateChanged', true));
    audioEl.addEventListener('pause', () => dotNetRef.invokeMethodAsync('OnAudioStateChanged', false));
    audioEl.addEventListener('ended', () => dotNetRef.invokeMethodAsync('OnAudioStateChanged', false));

    // timeupdate fires several times a second natively - plenty for a progress bar / "5% of
    // current position" nudge without needing a manual polling timer (the WPF app used a 100ms
    // DispatcherTimer for the same purpose).
    audioEl.addEventListener('timeupdate', () => {
        if (isFinite(audioEl.duration)) {
            dotNetRef.invokeMethodAsync('OnProgress', audioEl.currentTime, audioEl.duration);

            const bar = audioEl._lcSeekBar;
            if (bar && !(audioEl._lcSeekBarDragging && audioEl._lcSeekBarDragging())) {
                bar.value = audioEl.duration > 0 ? (audioEl.currentTime / audioEl.duration) * 100 : 0;
            }
        }
    });
}
