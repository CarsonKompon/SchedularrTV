// Undo/redo shortcuts for a canvas editor: Ctrl/⌘ + Z undoes, Ctrl/⌘ + Shift + Z or Ctrl + Y redoes, while the editor
// is on screen. Typing in a text field keeps the browser's own undo for that field.
export function attach(root, dotnet) {
    const events = new AbortController();
    const typing = target => {
        if (!(target instanceof Element)) return false;
        if (target.closest('[contenteditable=""], [contenteditable="true"]')) return true;
        if (target.tagName === 'TEXTAREA' || target.tagName === 'SELECT') return true;
        if (target.tagName !== 'INPUT') return false;
        // Checkboxes, sliders, colour pickers and buttons have no undo of their own.
        return !['checkbox', 'radio', 'range', 'color', 'button', 'submit', 'reset', 'file'].includes((target.type || '').toLowerCase());
    };
    const shown = () => root.isConnected && root.getClientRects().length > 0;
    document.addEventListener('keydown', e => {
        if (e.defaultPrevented || e.altKey || !(e.ctrlKey || e.metaKey) || !shown() || typing(e.target)) return;
        // Another dialog over the editor gets its keys.
        const dialog = e.target instanceof Element ? e.target.closest('.modal, dialog') : null;
        if (dialog && !dialog.contains(root)) return;
        const key = e.key.toLowerCase();
        const redo = (key === 'z' && e.shiftKey) || (key === 'y' && !e.metaKey && !e.shiftKey);
        const undo = key === 'z' && !e.shiftKey;
        if (!undo && !redo) return;
        e.preventDefault();
        dotnet.invokeMethodAsync(redo ? 'RedoShortcut' : 'UndoShortcut').catch(() => { });
    }, { signal: events.signal });
    return { dispose: () => events.abort() };
}
