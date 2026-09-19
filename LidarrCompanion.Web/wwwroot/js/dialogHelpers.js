export function focusAndSelect(element) {
    if (!element) return;
    element.focus();
    element.select();
}
