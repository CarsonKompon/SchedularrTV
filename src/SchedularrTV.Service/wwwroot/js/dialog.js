// Native modal dialogs provide focus containment, Escape dismissal, and focus restoration.
export function show(dialog) {
    if (!dialog.open) dialog.showModal();
}

export function close(dialog) {
    dialog.close();
}
