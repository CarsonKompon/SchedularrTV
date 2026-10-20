// Keep drag feedback local; one source-normalized selection goes to Blazor on release.
export function attach(id, receiver) {
    const svg = document.getElementById(id);
    svg.dataset.cleanupReady = 'true';
    const events = new AbortController();
    let start = null, rect = null;
    const point = e => {
        const bounds = svg.getBoundingClientRect();
        return { x: Math.max(0, Math.min(1, (e.clientX - bounds.left) / bounds.width)),
                 y: Math.max(0, Math.min(1, (e.clientY - bounds.top) / bounds.height)) };
    };
    const reset = () => { start = null; rect?.remove(); rect = null; };
    svg.addEventListener('pointerdown', e => {
        if (e.button !== 0) return;
        e.preventDefault(); reset(); start = point(e);
        rect = document.createElementNS('http://www.w3.org/2000/svg', 'rect');
        rect.setAttribute('fill', '#45dbff22'); rect.setAttribute('stroke', '#45dbff');
        rect.setAttribute('stroke-width', '2'); rect.setAttribute('vector-effect', 'non-scaling-stroke');
        rect.style.pointerEvents = 'none'; svg.appendChild(rect); svg.setPointerCapture(e.pointerId);
    }, { signal: events.signal });
    svg.addEventListener('pointermove', e => {
        if (!start) return;
        const end = point(e);
        for (const [key, value] of Object.entries({ x: Math.min(start.x,end.x), y: Math.min(start.y,end.y), width: Math.abs(end.x-start.x), height: Math.abs(end.y-start.y) }))
            rect.setAttribute(key, value * 1000);
    }, { signal: events.signal });
    svg.addEventListener('pointerup', e => {
        if (!start) return;
        const end = point(e), from = start;
        reset(); svg.releasePointerCapture(e.pointerId);
        receiver.invokeMethodAsync('SelectionMade', Math.min(from.x,end.x), Math.min(from.y,end.y), Math.abs(end.x-from.x), Math.abs(end.y-from.y)).catch(() => {});
    }, { signal: events.signal });
    svg.addEventListener('pointercancel', reset, { signal: events.signal });
    return { dispose() { events.abort(); reset(); delete svg.dataset.cleanupReady; } };
}
