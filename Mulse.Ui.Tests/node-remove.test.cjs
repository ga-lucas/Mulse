const { test } = require('node:test');
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const source = readFileSync(path.join(__dirname, '..', 'Mulse.Ui', 'wwwroot', 'js', 'flow-canvas.js'), 'utf8');

function setup() {
    const events = {};
    const calls = [];
    const captured = [];
    const context = vm.createContext({});
    vm.runInContext(source.replaceAll('export function ', 'function '), context);
    const group = {
        dataset: { nodeId: 'fetch-1' },
        getAttribute: () => 'translate(40, 50)',
        classList: { add() {}, remove() {} },
        setPointerCapture: id => captured.push(id),
        hasPointerCapture: () => false
    };
    const svg = {
        viewBox: { baseVal: { x: 0, y: 0, width: 1080, height: 520 } },
        getScreenCTM: () => null,
        addEventListener: (name, callback) => { events[name] = callback; },
        removeEventListener: name => { delete events[name]; }
    };
    context.initialize(svg, { invokeMethodAsync: (...args) => { calls.push(args); } });
    const pointer = (onButton, button = 0) => ({
        button, pointerId: 7, clientX: 100, clientY: 100, prevented: false,
        target: { closest: selector => selector === '.node-remove' ? (onButton ? {} : null) : selector === '.flow-node' ? group : null },
        preventDefault() { this.prevented = true; }
    });
    return { events, calls, captured, svg, pointer };
}

test('remove button and its icon do not drag, capture the pointer or prevent native focus/click', async () => {
    const app = setup();
    const event = app.pointer(true);
    app.events.pointerdown(event);
    assert.equal(app.svg.__flowCanvas.state.drag, null);
    assert.equal(event.prevented, false);
    assert.deepEqual(app.captured, []);
    app.events.pointermove(event);
    await app.events.pointerup(event);
    assert.deepEqual(app.calls, []);
});

test('node body still starts a normal drag', async () => {
    const app = setup();
    const event = app.pointer(false);
    app.events.pointerdown(event);
    assert.equal(app.svg.__flowCanvas.state.drag.kind, 'node');
    assert.equal(app.svg.__flowCanvas.state.drag.id, 'fetch-1');
    assert.equal(event.prevented, true);
    assert.deepEqual(app.captured, [7]);
    await app.events.pointerup(event);
    assert.equal(app.svg.__flowCanvas.state.drag, null);
    assert.deepEqual(app.calls, []);
});

test('right clicks remain ignored on nodes and remove controls', () => {
    const app = setup();
    for (const onButton of [true, false]) {
        const event = app.pointer(onButton, 2);
        app.events.pointerdown(event);
        assert.equal(event.prevented, false);
        assert.equal(app.svg.__flowCanvas.state.drag, null);
    }
    assert.deepEqual(app.captured, []);
});
