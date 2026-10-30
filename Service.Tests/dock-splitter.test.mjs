import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const source = await readFile(new URL('../Mulse.Ui/wwwroot/js/dock-splitter.js', import.meta.url), 'utf8');
const { initialize, dispose } = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);

function fixture() {
    const listeners = new Map();
    const attributes = new Map([['aria-valuenow', '45']]);
    const properties = new Map();
    const calls = [];
    const separator = {
        offsetHeight: 10, captured: null, focused: false,
        getAttribute: name => attributes.get(name),
        setAttribute: (name, value) => attributes.set(name, value),
        addEventListener: (name, handler) => listeners.set(name, handler),
        removeEventListener: name => listeners.delete(name),
        focus() { this.focused = true; },
        setPointerCapture(id) { this.captured = id; },
        hasPointerCapture(id) { return this.captured === id; },
        releasePointerCapture() { this.captured = null; }
    };
    const dock = { style: { setProperty: (name, value) => properties.set(name, value) }, getBoundingClientRect: () => ({ top: 100, height: 1010 }) };
    initialize(dock, separator, { invokeMethodAsync: (...args) => { calls.push(args); return Promise.resolve(); } });
    const send = (name, options = {}) => {
        const event = { pointerId: 1, button: 0, clientY: 605, prevented: false, preventDefault() { this.prevented = true; }, ...options };
        listeners.get(name)?.(event);
        return event;
    };
    return { separator, dock, listeners, attributes, properties, calls, send };
}

test('drag resizes both panes, clamps limits and commits only on release', () => {
    const f = fixture();
    assert.equal(f.send('pointerdown').prevented, true);
    assert.equal(f.separator.focused, true);
    assert.equal(f.separator.captured, 1);
    f.send('pointermove');
    assert.equal(f.properties.get('--toolbox-share'), '50fr');
    assert.equal(f.properties.get('--overview-share'), '50fr');
    f.send('pointermove', { clientY: -100 });
    assert.equal(f.attributes.get('aria-valuenow'), '20');
    f.send('pointermove', { clientY: 5000 });
    assert.equal(f.attributes.get('aria-valuenow'), '80');
    assert.equal(f.calls.length, 0);
    f.send('pointerup');
    assert.deepEqual(f.calls, [['OnDockSplitChanged', 80]]);
    assert.equal(f.separator.captured, null);
    f.send('lostpointercapture');
    assert.equal(f.calls.length, 1);
});

test('ignores other buttons/pointers and handles cancellation', () => {
    const f = fixture();
    f.send('pointerdown', { button: 2 });
    assert.equal(f.separator.captured, null);
    f.send('pointerdown');
    f.send('pointermove', { pointerId: 2, clientY: 900 });
    assert.equal(f.properties.size, 0);
    f.send('pointerup', { pointerId: 2 });
    assert.equal(f.calls.length, 0);
    f.send('pointermove');
    f.send('pointercancel');
    assert.deepEqual(f.calls, [['OnDockSplitChanged', 50]]);
});

test('preserves keyboard-updated value and does not intercept Tab', () => {
    const f = fixture();
    f.attributes.set('aria-valuenow', '65');
    f.send('pointerdown');
    f.send('pointerup');
    assert.deepEqual(f.calls, [['OnDockSplitChanged', 65]]);
    for (const key of ['ArrowUp', 'ArrowDown', 'Home', 'End']) assert.equal(f.send('keydown', { key }).prevented, true);
    assert.equal(f.send('keydown', { key: 'Tab' }).prevented, false);
});

test('initialization is idempotent and disposal removes all listeners', () => {
    const f = fixture();
    const handlers = f.separator.__dockSplitter;
    initialize(f.dock, f.separator, {});
    assert.equal(f.separator.__dockSplitter, handlers);
    dispose(f.separator);
    assert.equal(f.listeners.size, 0);
    assert.equal(f.separator.__dockSplitter, undefined);
    dispose(f.separator);
});
