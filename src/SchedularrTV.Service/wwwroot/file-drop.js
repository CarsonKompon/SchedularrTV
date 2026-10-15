// While files are dragged over the page, mark <html> with "files-dragging" so file pickers'
// hidden drop inputs (.file-drop-input) start catching drops. A drop anywhere else is ignored
// instead of the browser opening the file and leaving the app.
(() => {
    const root = document.documentElement;
    let depth = 0;
    const hasFiles = e => !!e.dataTransfer && Array.from(e.dataTransfer.types || []).includes("Files");
    const isFileInput = target => target instanceof HTMLInputElement && target.type === "file";
    const end = () => { depth = 0; root.classList.remove("files-dragging"); };

    window.addEventListener("dragenter", e => {
        if (!hasFiles(e)) return;
        depth++;
        root.classList.add("files-dragging");
    });
    window.addEventListener("dragleave", e => {
        if (!hasFiles(e)) return;
        depth = Math.max(0, depth - 1);
        if (depth === 0) root.classList.remove("files-dragging");
    });
    window.addEventListener("dragover", e => {
        if (hasFiles(e) && !isFileInput(e.target)) {
            e.preventDefault();
            e.dataTransfer.dropEffect = "none";
        }
    });
    window.addEventListener("drop", e => {
        if (!isFileInput(e.target)) e.preventDefault();
        end();
    });
    window.addEventListener("dragend", end);
})();
