import { test } from "node:test";
import assert from "node:assert/strict";
import { attach, detach } from "../src/SchedularrTV.Service/wwwroot/js/guide.js";

globalThis.ResizeObserver = class {
    observe() {}
    disconnect() {}
};

function fixture() {
    const listeners = new Map();
    const calls = [];
    const element = {
        scrollLeft: 900, clientWidth: 800,
        addEventListener(name, handler, options) { listeners.set(name, { handler, options }); },
        removeEventListener(name) { listeners.delete(name); },
        getBoundingClientRect() { return { left: 50 }; }
    };
    attach(element, { async invokeMethodAsync(...args) { calls.push(args); } });
    return { element, listeners, calls };
}

for (const modifier of ["shiftKey", "ctrlKey"]) {
    test(`${modifier} wheel zoom prevents browser scrolling and keeps the pointer anchor`, async () => {
        const { element, listeners, calls } = fixture();
        let prevented = false;
        try {
            const wheel = listeners.get("wheel");
            assert.equal(wheel.options.passive, false);
            await wheel.handler({ [modifier]: true, deltaY: -100, deltaX: 0, clientX: 450, preventDefault() { prevented = true; } });
            assert.equal(prevented, true);
            assert.deepEqual(calls, [["ZoomAt", 1, 900, 400]]);
        } finally { detach(element); }
        assert.equal(listeners.size, 0);
    });
}

test("unmodified wheel is left to native scrolling", async () => {
    const { element, listeners, calls } = fixture();
    try {
        await listeners.get("wheel").handler({ deltaY: 100, deltaX: 0, preventDefault() { assert.fail("prevented native scroll"); } });
        assert.equal(calls.length, 0);
    } finally { detach(element); }
});

test("shift horizontal wheel can zoom out", async () => {
    const { element, listeners, calls } = fixture();
    try {
        await listeners.get("wheel").handler({ shiftKey: true, deltaY: 0, deltaX: 100, clientX: 450, preventDefault() {} });
        assert.deepEqual(calls, [["ZoomAt", -1, 900, 400]]);
    } finally { detach(element); }
});
