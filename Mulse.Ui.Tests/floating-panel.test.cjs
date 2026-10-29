const { test } = require('node:test');
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const source = readFileSync(path.join(__dirname, '..', 'Mulse.Ui', 'wwwroot', 'js', 'floating-panel.js'), 'utf8');

function setup({ position = 'absolute' } = {}) {
    function element() {
        const events = {};
        const properties = new Map();
        return {
            events,
            style: { setProperty: (name, value) => properties.set(name, value) },
            addEventListener: (name, callback) => { events[name] = callback; },
            removeEventListener: (name, callback) => {
                if (events[name] === callback) delete events[name];
            },
            get offsetLeft() { return Math.max(16, parseFloat(properties.get('--panel-x') ?? '24')); },
            get offsetTop() { return Math.max(16, parseFloat(properties.get('--panel-y') ?? '24')); },
            properties
        };
    }
    const panel = element();
    const header = element();
    const captures = new Set();
    header.setPointerCapture = id => captures.add(id);
    header.hasPointerCapture = id => captures.has(id);
    header.releasePointerCapture = id => captures.delete(id);
    const moves = [];
    const context = vm.createContext({ getComputedStyle: () => ({ position }) });
    vm.runInContext(source.replaceAll('export function ', 'function '), context);
    const initialize = () => context.initialize(panel, header, {
        invokeMethodAsync: async (...args) => { moves.push(args); }
    });
    initialize();
    const event = (x, y, overrides = {}) => ({
        pointerId: 1, button: 0, clientX: x, clientY: y,
        target: { closest: () => null }, preventDefault() {}, ...overrides
    });
    return { panel, header, captures, moves, event, initialize, dispose: () => context.dispose(panel) };
}

test('drags using the rendered position variables and reports the final position once', async () => {
    const app = setup();
    app.header.events.pointerdown(app.event(100, 100));
    app.header.events.pointermove(app.event(200, 250));
    assert.equal(app.panel.properties.get('--panel-x'), '124px');
    assert.equal(app.panel.properties.get('--panel-y'), '174px');
    assert.equal(app.panel.style.left, undefined);
    assert.equal(app.panel.style.top, undefined);
    assert.deepEqual(app.moves, []);
    await app.header.events.pointerup(app.event(200, 250));
    assert.deepEqual(app.moves, [['OnMoved', 124, 174]]);
    assert.equal(app.captures.size, 0);
    await app.header.events.pointerup(app.event(200, 250));
    assert.equal(app.moves.length, 1);

    app.header.events.pointerdown(app.event(200, 250));
    app.header.events.pointermove(app.event(220, 280));
    await app.header.events.pointerup(app.event(220, 280));
    assert.deepEqual(app.moves[1], ['OnMoved', 144, 204]);
});

test('reports CSS-constrained coordinates when a drag is cancelled', async () => {
    const app = setup();
    app.header.events.pointerdown(app.event(100, 100));
    app.header.events.pointermove(app.event(-100, -100));
    assert.equal(app.panel.properties.get('--panel-x'), '0px');
    await app.header.events.pointercancel(app.event(-100, -100));
    assert.deepEqual(app.moves, [['OnMoved', 16, 16]]);
    assert.equal(app.captures.size, 0);
});

test('ignores clicks, unrelated pointers, controls, and non-primary buttons', async () => {
    const app = setup();
    app.header.events.pointerdown(app.event(100, 100));
    app.header.events.pointermove(app.event(200, 200, { pointerId: 2 }));
    await app.header.events.pointerup(app.event(200, 200, { pointerId: 2 }));
    await app.header.events.pointerup(app.event(100, 100));
    assert.deepEqual(app.moves, []);
    assert.equal(app.panel.properties.size, 0);
    for (const overrides of [{ button: 2 }, { target: { closest: () => ({}) } }]) {
        app.header.events.pointerdown(app.event(100, 100, overrides));
        app.header.events.pointermove(app.event(200, 200));
        await app.header.events.pointerup(app.event(200, 200));
    }
    assert.deepEqual(app.moves, []);
    assert.equal(app.panel.properties.size, 0);
});

test('leaves stacked panels in their responsive layout', async () => {
    const app = setup({ position: 'relative' });
    app.header.events.pointerdown(app.event(100, 100));
    app.header.events.pointermove(app.event(200, 200));
    await app.header.events.pointerup(app.event(200, 200));
    assert.equal(app.panel.properties.size, 0);
    assert.deepEqual(app.moves, []);
    assert.equal(app.captures.size, 0);
});

test('initializes once and removes handlers on disposal', () => {
    const app = setup();
    const original = app.header.events.pointerdown;
    app.initialize();
    assert.equal(app.header.events.pointerdown, original);
    app.dispose();
    assert.deepEqual(Object.keys(app.header.events), []);
    assert.deepEqual(Object.keys(app.panel.events), []);
    assert.equal(app.panel.__floatingPanel, undefined);
    app.dispose();
    app.initialize();
    assert.equal(typeof app.header.events.pointerdown, 'function');
});
