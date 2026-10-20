import { test } from 'node:test';
import assert from 'node:assert/strict';
import { setTimeout as wait } from 'node:timers/promises';
import { latestOnly } from '../src/SchedularrTV.Service/wwwroot/js/canvas-interactions.js';

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
