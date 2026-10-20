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

export function constrainResize(width, height, dx, dy, lockAspect) {
    if (!lockAspect) return { dx: Math.max(2, width + dx) - width, dy: Math.max(2, height + dy) - height };
    const change = Math.abs(dx / width) >= Math.abs(dy / height) ? dx / width : dy / height;
    const scale = Math.max(2 / width, 2 / height, 1 + change);
    return { dx: width * scale - width, dy: height * scale - height };
}

export function rulerTime(clientX, left, width, duration, fps) {
    const last = Math.max(0, duration - 1 / fps);
    return Math.min(last, Math.max(0, Math.round((clientX - left) / Math.max(1, width) * duration * fps) / fps));
}

export function attach(root, dotnet) {
    const events = new AbortController();
    const on = (element, type, handler, options = {}) => element.addEventListener(type, handler, { ...options, signal: events.signal });
    let drag = null, timeline = null, sequence = 0;
    const queue = latestOnly(value => dotnet.invokeMethodAsync('UpdateInteraction', value));
    on(root, 'keydown', e => {
        if (!e.target.matches('[data-squeeze-ruler]')) return;
        const commands = { ArrowLeft: 'left', ',': 'left', '<': 'left', ArrowRight: 'right', '.': 'right', '>': 'right',
            Home: 'home', End: 'end', '[': 'previous', ']': 'next', k: 'add', K: 'add', Escape: 'escape', Delete: 'delete', Backspace: 'delete' };
        const command = commands[e.key];
        if (!command) return;
        e.preventDefault(); e.stopPropagation();
        queue.push({ kind: 'command', ratio: root.dataset.canvasRatio, command, range: e.shiftKey, final: true });
    });
    on(root, 'pointerdown', e => {
        const ruler = e.target.closest('[data-squeeze-ruler]');
        if (ruler && e.button === 0) {
            e.preventDefault(); ruler.focus({ preventScroll: true });
            const bounds = ruler.getBoundingClientRect(), key = e.target.closest('[data-squeeze-key]');
            timeline = { id: ++sequence, ratio: root.dataset.canvasRatio, ruler, left: bounds.left, width: bounds.width,
                duration: Number(ruler.dataset.duration), fps: Number(ruler.dataset.fps), pointer: e.pointerId, sx: e.clientX, lastX: e.clientX,
                key: key ? Number(key.dataset.squeezeKey) : -1, layer: key ? Number(key.dataset.squeezeKeyLayer) : -1,
                toggle: e.ctrlKey || e.metaKey, range: e.shiftKey };
            root.setPointerCapture(e.pointerId);
            moveTimeline(e, false);
            return;
        }
        const box = e.target.closest('[data-squeeze-layer]');
        if (!box || box.classList.contains('locked') || e.button !== 0) return;
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
    function moveTimeline(e, final) {
        if (!timeline || timeline.pointer !== e.pointerId) return;
        timeline.lastX = e.clientX;
        if (timeline.key < 0) {
            const time = rulerTime(e.clientX, timeline.left, timeline.width, timeline.duration, timeline.fps);
            timeline.ruler.querySelector('.ed-playhead').style.left = `${time / timeline.duration * 100}%`;
            queue.push({ kind: 'scrub', ratio: timeline.ratio, time, final });
        } else {
            queue.push({ kind: 'keys', ratio: timeline.ratio, id: timeline.id, layer: timeline.layer, key: timeline.key,
                toggle: timeline.toggle, range: timeline.range, delta: (e.clientX - timeline.sx) / Math.max(1, timeline.width) * timeline.duration, final });
        }
        if (final) { timeline = null; if (root.hasPointerCapture(e.pointerId)) root.releasePointerCapture(e.pointerId); }
    }
    function move(e, final) {
        if (!drag || drag.pointer !== e.pointerId) return;
        drag.lastX = e.clientX; drag.lastY = e.clientY; drag.shiftKey = !!e.shiftKey;
        let dx = e.clientX - drag.sx, dy = e.clientY - drag.sy;
        if (drag.resize) {
            const constrained = constrainResize(drag.width / 100 * drag.canvasWidth * drag.scale,
                drag.height / 100 * drag.canvasHeight * drag.scale, dx * drag.scale, dy * drag.scale, drag.shiftKey);
            dx = constrained.dx / drag.scale; dy = constrained.dy / drag.scale;
        }
        // Immediate local outline feedback, independent of network/render latency.
        if (drag.resize) {
            drag.box.style.width = `${drag.width + dx / drag.canvasWidth * 100}%`;
            drag.box.style.height = `${drag.height + dy / drag.canvasHeight * 100}%`;
        } else {
            drag.box.style.left = `${drag.x + dx / drag.canvasWidth * 100}%`;
            drag.box.style.top = `${drag.y + dy / drag.canvasHeight * 100}%`;
        }
        queue.push({ kind: 'drag', ratio: drag.ratio, id: drag.id, layer: drag.layer, resize: drag.resize, dx: dx * drag.scale, dy: dy * drag.scale, lockAspect: drag.shiftKey, final });
        if (final) { drag = null; if (root.hasPointerCapture(e.pointerId)) root.releasePointerCapture(e.pointerId); }
    }
    on(root, 'pointermove', e => { move(e, false); moveTimeline(e, false); });
    on(root, 'pointerup', e => { move(e, true); moveTimeline(e, true); });
    const finishLast = () => {
        if (drag) move({ pointerId: drag.pointer, clientX: drag.lastX, clientY: drag.lastY, shiftKey: drag.shiftKey }, true);
        if (timeline) moveTimeline({ pointerId: timeline.pointer, clientX: timeline.lastX }, true);
    };
    on(root, 'pointercancel', finishLast);
    on(root, 'lostpointercapture', finishLast);
    return { dispose() { events.abort(); queue.dispose(); drag = null; timeline = null; } };
}
