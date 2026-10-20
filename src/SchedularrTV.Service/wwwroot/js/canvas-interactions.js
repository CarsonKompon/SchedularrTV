// Backpressure for server-rendered editors: one call in flight and only the newest
// pending position. Pointer events must never accumulate behind expensive frames.
export function latestOnly(send, delay = 40) {
    let pending, running = false, timer, disposed = false;
    const endings = [];
    async function flush() {
        timer = undefined;
        if (disposed || running || (!pending && !endings.length)) return;
        const value = endings.length ? endings.shift() : pending;
        if (value === pending) pending = undefined;
        running = true;
        try { await send(value); }
        catch { pending = undefined; endings.length = 0; }
        finally {
            running = false;
            if (!disposed && (pending || endings.length)) timer = setTimeout(flush, endings.length || pending?.final ? 0 : delay);
        }
    }
    return {
        push(value) {
            if (disposed) return;
            if (pending?.final) endings.push(pending);
            pending = value;
            if (!running && (timer === undefined || value.final)) {
                if (timer !== undefined) clearTimeout(timer);
                timer = setTimeout(flush, value.final ? 0 : delay);
            }
        },
        dispose() { disposed = true; pending = undefined; endings.length = 0; clearTimeout(timer); }
    };
}

export function attach(root, dotnet) {
    const events = new AbortController();
    const on = (element, type, handler, options = {}) => element.addEventListener(type, handler, { ...options, signal: events.signal });
    let drag = null, sequence = 0;
    const queue = latestOnly(value => dotnet.invokeMethodAsync('UpdateInteraction', value));
    on(root, 'input', e => {
        if (e.target.matches('[data-squeeze-scrub]')) queue.push({ kind: 'scrub', ratio: root.dataset.canvasRatio, time: Number(e.target.value), final: false });
    });
    on(root, 'change', e => {
        if (e.target.matches('[data-squeeze-scrub]')) queue.push({ kind: 'scrub', ratio: root.dataset.canvasRatio, time: Number(e.target.value), final: true });
    });
    on(root, 'pointerdown', e => {
        const box = e.target.closest('[data-squeeze-layer]');
        if (!box || e.button !== 0) return;
        e.preventDefault();
        const canvas = box.closest('.ed-canvas');
        const bounds = canvas.getBoundingClientRect();
        drag = { id: ++sequence, ratio: root.dataset.canvasRatio, layer: Number(box.dataset.squeezeLayer), resize: !!e.target.closest('[data-squeeze-resize]'),
            sx: e.clientX, sy: e.clientY, lastX: e.clientX, lastY: e.clientY, scale: Number(root.dataset.canvasWidth) / bounds.width,
            x: parseFloat(box.style.left), y: parseFloat(box.style.top), width: parseFloat(box.style.width), height: parseFloat(box.style.height),
            canvasWidth: bounds.width, canvasHeight: bounds.height, box, pointer: e.pointerId };
        root.setPointerCapture(e.pointerId);
        queue.push({ kind: 'drag', ratio: drag.ratio, id: drag.id, layer: drag.layer, resize: drag.resize, dx: 0, dy: 0, final: false });
    });
    function move(e, final) {
        if (!drag || drag.pointer !== e.pointerId) return;
        drag.lastX = e.clientX; drag.lastY = e.clientY;
        const dx = e.clientX - drag.sx, dy = e.clientY - drag.sy;
        // Immediate local outline feedback, independent of network/render latency.
        if (drag.resize) {
            drag.box.style.width = `${Math.max(.1, drag.width + dx / drag.canvasWidth * 100)}%`;
            drag.box.style.height = `${Math.max(.1, drag.height + dy / drag.canvasHeight * 100)}%`;
        } else {
            drag.box.style.left = `${drag.x + dx / drag.canvasWidth * 100}%`;
            drag.box.style.top = `${drag.y + dy / drag.canvasHeight * 100}%`;
        }
        queue.push({ kind: 'drag', ratio: drag.ratio, id: drag.id, layer: drag.layer, resize: drag.resize, dx: dx * drag.scale, dy: dy * drag.scale, final });
        if (final) { drag = null; if (root.hasPointerCapture(e.pointerId)) root.releasePointerCapture(e.pointerId); }
    }
    on(root, 'pointermove', e => move(e, false));
    on(root, 'pointerup', e => move(e, true));
    const finishLast = () => { if (drag) move({ pointerId: drag.pointer, clientX: drag.lastX, clientY: drag.lastY }, true); };
    on(root, 'pointercancel', finishLast);
    on(root, 'lostpointercapture', finishLast);
    return { dispose() { events.abort(); queue.dispose(); drag = null; } };
}
