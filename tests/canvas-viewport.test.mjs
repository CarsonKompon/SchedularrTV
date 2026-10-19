import { test } from 'node:test';
import assert from 'node:assert/strict';
import { create } from '../src/SchedularrTV.Service/wwwroot/js/canvas-viewport.js';

function fixture() {
    const listeners = new Map();
    const signals = [];
    const classes = new Set();
    const badge = {};
    const style = { setProperty(name, value) { this[name] = value; } };
    let resized, disconnected = false;
    const viewport = {
        clientWidth: 800, clientHeight: 450, isConnected: true,
        querySelector: () => badge,
        matches: () => true,
        getBoundingClientRect: () => ({ left: 0, top: 0 }),
        setPointerCapture() {},
        classList: {
            toggle(name, enabled) { enabled ? classes.add(name) : classes.delete(name); },
            add(name) { classes.add(name); }, remove(name) { classes.delete(name); }
        },
        addEventListener(name, handler, options) {
            listeners.set(name, handler);
            signals.push(options.signal);
        }
    };
    globalThis.document = {
        getElementById: id => id === 'viewport' ? viewport : { style },
        addEventListener: viewport.addEventListener
    };
    globalThis.ResizeObserver = class {
        constructor(callback) { resized = callback; }
        observe() {}
        disconnect() { disconnected = true; }
    };
    const zoom = create('viewport', 'canvas');
    const wheel = (overrides = {}) => listeners.get('wheel')({
        ctrlKey: true, deltaY: -Math.log(2) / .0015, deltaMode: 0,
        clientX: 400, clientY: 225, preventDefault() {}, ...overrides
    });
    return { zoom, wheel, viewport, style, badge, signals, listeners, classes,
        resize: () => resized(), disconnected: () => disconnected };
}

test('zoom keeps the point under the pointer fixed and fit clears transforms', () => {
    const f = fixture();
    f.wheel();
    assert.equal(f.style.transform, 'translate(-400px, -225px) scale(2)');
    assert.equal(f.badge.textContent, '200%');
    f.zoom.reset();
    assert.equal(f.style.transform, '');
    assert.equal(f.style['--zoom'], 1);
    assert.equal(f.badge.textContent, '');
    f.zoom.dispose();
});

test('normal scroll is preserved; resize clamps panning after inspector changes', () => {
    const f = fixture();
    f.wheel({ ctrlKey: false, preventDefault() { assert.fail('native scroll prevented'); } });
    assert.equal(f.style.transform, undefined);
    f.wheel({ ctrlKey: false, metaKey: true });
    f.viewport.clientWidth = 200;
    f.viewport.clientHeight = 100;
    f.resize();
    assert.equal(f.style.transform, 'translate(-200px, -100px) scale(2)');
    f.zoom.dispose();
});

test('disposing a switched template releases document listeners, resize observer and pan state', () => {
    const f = fixture();
    f.listeners.get('pointerdown')({ button: 1, clientX: 10, clientY: 10, pointerId: 1,
        preventDefault() {}, stopPropagation() {} });
    assert.equal(f.classes.has('panning'), true);
    f.zoom.dispose();
    assert.ok(f.signals.every(signal => signal.aborted));
    assert.equal(f.disconnected(), true);
    assert.equal(f.classes.has('panning'), false);
});
