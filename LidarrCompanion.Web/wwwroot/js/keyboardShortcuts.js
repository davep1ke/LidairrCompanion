// Reproduces the WPF app's keyboard shortcuts (Blazor has no RoutedCommand/KeyBinding
// equivalent). Two independent listeners since the two pages use different key conventions:
// the triage screen uses Alt+<key> (register/unregister), Sift uses bare keys with no modifier
// (registerSift/unregisterSift) - only one of the two pages is ever mounted at a time, but they're
// kept as separate functions/listeners to match that each page's shortcuts are only meaningful
// while that page is showing.

// Triage screen. Every key works as Alt+<key>; m/x/u (Match / delete / Unlink - the three
// per-file actions used constantly while working through a release) also work bare, as long as
// focus isn't in something you type into.
const altShortcutKeys = new Set(['1', 'a', 'm', 'p', 'x', 'u']);
const bareShortcutKeys = new Set(['m', 'x', 'u']);
let altHandler = null;

function isTypingTarget(el) {
    if (!el) return false;
    const tag = el.tagName;
    return tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || el.isContentEditable === true;
}

export function register(dotNetRef) {
    unregister();

    altHandler = (e) => {
        if (e.ctrlKey || e.metaKey) return;
        const key = e.key.toLowerCase();

        if (e.altKey) {
            if (!altShortcutKeys.has(key)) return;
        } else {
            // A held-down key would otherwise fire the action repeatedly - one press, one action.
            if (e.repeat || !bareShortcutKeys.has(key) || isTypingTarget(document.activeElement)) return;
        }

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

// Scroll helpers used after the triage page re-renders following a selection change.
export function scrollToTop(selector) {
    const el = document.querySelector(selector);
    if (el) el.scrollTop = 0;
}

export function scrollSelectedIntoView(containerSelector, rowSelector) {
    const container = document.querySelector(containerSelector);
    const row = container?.querySelector(rowSelector);
    if (row) row.scrollIntoView({ block: 'nearest' });
}
