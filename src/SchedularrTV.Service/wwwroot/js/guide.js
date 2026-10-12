const guides = new WeakMap();

export function attach(element, receiver) {
    detach(element);
    let timer, busy = false, disposed = false, lastZoom = -Infinity;
    const report = () => {
        clearTimeout(timer);
        timer = setTimeout(() => {
            if (!disposed) receiver.invokeMethodAsync("ViewportChanged", element.scrollLeft, element.clientWidth).catch(() => {});
        }, 80);
    };
    const wheel = async event => {
        if (!event.shiftKey && !event.ctrlKey) return;
        event.preventDefault();
        if (busy || disposed || performance.now() - lastZoom < 120) return;
        const delta = Math.abs(event.deltaY) >= Math.abs(event.deltaX) ? event.deltaY : event.deltaX;
        if (!delta) return;
        busy = true;
        lastZoom = performance.now();
        try {
            await receiver.invokeMethodAsync("ZoomAt", delta < 0 ? 1 : -1, element.scrollLeft,
                event.clientX - element.getBoundingClientRect().left);
        } catch { /* The circuit may have disconnected. */ }
        finally { busy = false; }
    };
    const observer = new ResizeObserver(report);
    observer.observe(element);
    element.addEventListener("scroll", report, { passive: true });
    element.addEventListener("wheel", wheel, { passive: false });
    guides.set(element, () => {
        disposed = true;
        clearTimeout(timer);
        observer.disconnect();
        element.removeEventListener("scroll", report);
        element.removeEventListener("wheel", wheel);
    });
    report();
}

export function restore(element, left) { element.scrollLeft = left; }
export function detach(element) { guides.get(element)?.(); guides.delete(element); }
