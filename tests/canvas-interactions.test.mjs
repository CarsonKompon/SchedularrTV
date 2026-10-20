import { test } from 'node:test';
import assert from 'node:assert/strict';
import { setTimeout as wait } from 'node:timers/promises';
import { latestOnly, constrainResize, rulerTime, attach } from '../src/SchedularrTV.Service/wwwroot/js/canvas-interactions.js';

test('timeline pointer drags preserve key identity and modifiers through the client bridge', async () => {
    const listeners = new Map(), sent = [];
    const ruler = { dataset: { duration: '10', fps: '30' }, getBoundingClientRect: () => ({ left: 10, width: 1000 }), focus() {}, matches: () => true };
    const marker = { dataset: { squeezeKey: '2', squeezeKeyLayer: '3' } };
    const target = { closest: selector => selector === '[data-squeeze-ruler]' ? ruler : selector === '[data-squeeze-key]' ? marker : null };
    const root = { dataset: { canvasRatio: '16:9' }, addEventListener(name, action) { listeners.set(name, action); },
        setPointerCapture() {}, hasPointerCapture: () => true, releasePointerCapture() {} };
    const handle = attach(root, { async invokeMethodAsync(name, value) { sent.push(value); } });
    const event = { target, button: 0, pointerId: 1, clientX: 110, ctrlKey: true, shiftKey: false, preventDefault() {} };
    listeners.get('pointerdown')(event);
    listeners.get('pointermove')({ ...event, clientX: 310 });
    listeners.get('pointerup')({ ...event, clientX: 310 });
    await wait(30);
    assert.equal(sent.length, 1);
    assert.equal(sent[0].kind, 'keys');
    assert.equal(sent[0].key, 2);
    assert.equal(sent[0].layer, 3);
    assert.equal(sent[0].toggle, true);
    assert.equal(sent[0].delta, 2);
    assert.equal(sent[0].final, true);
    handle.dispose();
});

test('ruler scrubbing maps pointer position to snapped frames and clamps the ends', () => {
    assert.equal(rulerTime(350, 100, 500, 10, 30), 5);
    assert.equal(rulerTime(0, 100, 500, 10, 30), 0);
    assert.equal(rulerTime(900, 100, 500, 10, 30), 10 - 1 / 30);
    assert.equal(rulerTime(351, 100, 500, 10, 30), 151 / 30);
});

test('Shift resize preserves the starting ratio for horizontal, vertical and shrinking drags', () => {
    for (const [dx, dy] of [[160, 5], [10, 90], [-800, -800]]) {
        const delta = constrainResize(160, 90, dx, dy, true);
        const width = 160 + delta.dx, height = 90 + delta.dy;
        assert.ok(Math.abs(width / height - 16 / 9) < 1e-8);
        assert.ok(width >= 2 && height >= 2);
    }
    assert.deepEqual(constrainResize(160, 90, 40, 10, false), { dx: 40, dy: 10 });
});

test('slow render keeps one request in flight and sends only the newest movement', async () => {
    const sent = [];
    let release;
    const queue = latestOnly(async value => {
        sent.push(value);
        if (sent.length === 1) await new Promise(resolve => { release = resolve; });
    }, 1);
    queue.push({ x: 0 });
    await wait(20);
    for (let x = 1; x <= 200; x++) queue.push({ x });
    await wait(10);
    assert.deepEqual(sent, [{ x: 0 }]);
    release();
    await wait(20);
    assert.deepEqual(sent, [{ x: 0 }, { x: 200 }]);
    queue.dispose();
});

test('release is delivered before a following gesture even while the server is busy', async () => {
    const sent = [];
    let release;
    const queue = latestOnly(async value => {
        sent.push(value);
        if (sent.length === 1) await new Promise(resolve => { release = resolve; });
    }, 1);
    queue.push({ id: 1, x: 0 });
    await wait(20);
    queue.push({ id: 1, x: 100, final: true });
    queue.push({ id: 2, x: 0 });
    queue.push({ id: 2, x: 50, final: true });
    release();
    await wait(30);
    assert.deepEqual(sent, [{ id: 1, x: 0 }, { id: 1, x: 100, final: true }, { id: 2, x: 50, final: true }]);
    queue.dispose();
});

test('continuous input is throttled, not deferred until movement stops', async () => {
    const sent = [];
    const queue = latestOnly(async value => sent.push(value), 10);
    for (let x = 0; x < 20; x++) { queue.push({ x }); await wait(3); }
    assert.ok(sent.length >= 2, 'Frames should render during the gesture');
    assert.ok(sent.length < 20, 'Intermediate positions should be dropped');
    queue.dispose();
});

test('disposing drops pending work and prevents later sends', async () => {
    const sent = [];
    const queue = latestOnly(async value => sent.push(value), 10);
    queue.push({ x: 1 });
    queue.dispose();
    queue.push({ x: 2, final: true });
    await wait(20);
    assert.deepEqual(sent, []);
});
