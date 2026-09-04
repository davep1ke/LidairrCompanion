// Ctrl+V image paste for the /cover-art page, replacing the WPF app's Clipboard.GetImage()
// keyboard handler. Only one cover-art page is ever meaningfully active at a time (matching the
// app's single-active-session assumption), so a single module-scoped handler is enough - same
// register/unregister shape as keyboardShortcuts.js.

let pasteHandler = null;

export function registerPaste(dotNetRef) {
    unregisterPaste();

    pasteHandler = async (e) => {
        const items = e.clipboardData && e.clipboardData.items;
        if (!items) return;

        for (const item of items) {
            if (item.type && item.type.startsWith('image/')) {
                const blob = item.getAsFile();
                if (!blob) continue;

                const buffer = await blob.arrayBuffer();
                const bytes = new Uint8Array(buffer);
                let binary = '';
                for (let i = 0; i < bytes.length; i++) binary += String.fromCharCode(bytes[i]);
                const base64 = btoa(binary);

                e.preventDefault();
                await dotNetRef.invokeMethodAsync('OnImagePasted', base64, item.type);
                return;
            }
        }
    };

    document.addEventListener('paste', pasteHandler);
}

export function unregisterPaste() {
    if (pasteHandler) {
        document.removeEventListener('paste', pasteHandler);
        pasteHandler = null;
    }
}
