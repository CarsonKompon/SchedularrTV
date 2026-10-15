export function attach(element) {
    const button = element.querySelector('[data-eyedropper]');
    const status = element.querySelector('[data-eyedropper-status]');
    const input = element.querySelector('input');
    if (!window.isSecureContext || !window.EyeDropper) {
        const message = 'Screen picking requires a supported browser (Chrome or Edge) on HTTPS or localhost.';
        button.title = message;
        status.textContent = message;
        return;
    }
    button.disabled = false;
    button.title = 'Pick a colour anywhere on your screen; Escape cancels';
    // Open directly in the browser click handler to preserve transient user activation.
    button.addEventListener('click', async () => {
        status.textContent = '';
        try {
            const result = await new EyeDropper().open();
            if (!element.isConnected) return;
            input.value = result.sRGBHex;
            input.dispatchEvent(new Event('change', { bubbles: true }));
        } catch (error) {
            if (error.name !== 'AbortError') status.textContent = 'Screen picking could not start. Try again or use the colour selector.';
        }
    });
}
