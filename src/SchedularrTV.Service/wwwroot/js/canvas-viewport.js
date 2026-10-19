// Shared pointer-centred zoom/pan for both design editors. Canvas bounds include the
// transform, so each editor's drag/resize/rotate calculations keep using screen pixels.
export function create(viewportId, canvasId) {
    const viewport = document.getElementById(viewportId);
    const canvas = document.getElementById(canvasId);
    const badge = viewport.querySelector('.ed-zoom-badge');
    const events = new AbortController();
    const on = (target, type, handler, options = {}) =>
        target.addEventListener(type, handler, { ...options, signal: events.signal });
    let zoom = 1, x = 0, y = 0, pan = null;
    const apply = () => {
        x = Math.min(0, Math.max(viewport.clientWidth * (1 - zoom), x));
        y = Math.min(0, Math.max(viewport.clientHeight * (1 - zoom), y));
        canvas.style.transformOrigin = '0 0';
        canvas.style.transform = zoom === 1 ? '' : `translate(${x}px, ${y}px) scale(${zoom})`;
        canvas.style.setProperty('--zoom', zoom);
        viewport.classList.toggle('zoomed', zoom !== 1);
        badge.textContent = zoom === 1 ? '' : `${Math.round(zoom * 100)}%`;
    };
    const zoomAt = (next, px, py) => {
        next = Math.min(16, Math.max(1, next));
        x = px - (px - x) * next / zoom;
        y = py - (py - y) * next / zoom;
        zoom = next;
        apply();
    };
    on(viewport, 'wheel', e => {
        if (!e.ctrlKey && !e.metaKey) return;
        e.preventDefault();
        const r = viewport.getBoundingClientRect();
        zoomAt(zoom * Math.exp(-e.deltaY * (e.deltaMode === 1 ? 0.05 : 0.0015)), e.clientX - r.left, e.clientY - r.top);
    }, { passive: false });
    on(viewport, 'pointerdown', e => {
        if (e.button !== 1) return;
        e.preventDefault();
        e.stopPropagation();
        pan = { sx: e.clientX, sy: e.clientY, x, y };
        viewport.setPointerCapture(e.pointerId);
        viewport.classList.add('panning');
    }, { capture: true });
    on(viewport, 'mousedown', e => { if (e.button === 1) e.preventDefault(); }, { capture: true });
    on(viewport, 'auxclick', e => { if (e.button === 1) e.preventDefault(); }, { capture: true });
    on(viewport, 'pointermove', e => {
        if (!pan) return;
        x = pan.x + e.clientX - pan.sx;
        y = pan.y + e.clientY - pan.sy;
        apply();
    });
    const end = () => { pan = null; viewport.classList.remove('panning'); };
    on(viewport, 'pointerup', end);
    on(viewport, 'pointercancel', end);
    on(viewport, 'lostpointercapture', end);
    on(document, 'keydown', e => {
        if ((e.ctrlKey || e.metaKey) && e.key === '0' && viewport.isConnected && viewport.matches(':hover')) {
            e.preventDefault();
            zoomAt(1, 0, 0);
        }
    });
    const resize = new ResizeObserver(apply);
    resize.observe(viewport);
    return {
        reset: () => zoomAt(1, 0, 0),
        dispose: () => { events.abort(); resize.disconnect(); end(); }
    };
}
