// Reproduces the WPF app's keyboard shortcuts (Blazor has no RoutedCommand/KeyBinding
// equivalent). Two independent listeners since the two pages use different key conventions:
// the triage screen uses Alt+<key> (register/unregister), Sift uses bare keys with no modifier
// (registerSift/unregisterSift) - only one of the two pages is ever mounted at a time, but they're
// kept as separate functions/listeners to match that each page's shortcuts are only meaningful
// while that page is showing.

const altShortcutKeys = new Set(['1', '3', 'a', 'm', 'p']);
let altHandler = null;

export function register(dotNetRef) {
    unregister();

    altHandler = (e) => {
        if (!e.altKey || e.ctrlKey || e.metaKey) return;
        const key = e.key.toLowerCase();
        if (!altShortcutKeys.has(key)) return;

        e.preventDefault();
        dotNetRef.invokeMethodAsync('OnShortcut', key);
    };

    document.addEventListener('keydown', altHandler);
}

export function unregister() {
    if (altHandler) {
        document.removeEventListener('keydown', altHandler);
        altHandler = null;
    }
}

const siftShortcutKeys = new Set([' ', 'y', 'n', '1', '2', '3', '4', '5', '6', '+', '=', '-', '_']);
let siftHandler = null;

export function registerSift(dotNetRef) {
    unregisterSift();

    siftHandler = (e) => {
        if (e.altKey || e.ctrlKey || e.metaKey) return;
        // Don't hijack typing into a text input/textarea elsewhere on the page.
        const tag = document.activeElement?.tagName;
        if (tag === 'INPUT' || tag === 'TEXTAREA') return;

        const key = e.key === ' ' ? ' ' : e.key.toLowerCase();
        if (!siftShortcutKeys.has(key)) return;

        e.preventDefault();
        dotNetRef.invokeMethodAsync('OnSiftShortcut', key);
    };

    document.addEventListener('keydown', siftHandler);
}

export function unregisterSift() {
    if (siftHandler) {
        document.removeEventListener('keydown', siftHandler);
        siftHandler = null;
    }
}
