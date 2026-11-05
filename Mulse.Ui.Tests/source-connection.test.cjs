const { test } = require('node:test');
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const source = readFileSync(path.join(__dirname, '..', 'Mulse.Ui', 'wwwroot', 'js', 'flow-canvas.js'), 'utf8');

function setup() {
    const events = {}, calls = [], data = new Map();
    const context = vm.createContext({});
    vm.runInContext(source.replaceAll('export function ', 'function '), context);
    const svg = {
        viewBox: { baseVal: { x: 0, y: 0, width: 1080, height: 520 } },
        addEventListener: (name, callback) => { events[name] = callback; },
        removeEventListener: name => { delete events[name]; }
    };
    context.initialize(svg, { invokeMethodAsync: (...args) => { calls.push(args); return Promise.resolve(); } });
    const transfer = { types: [], setData(type, value) { data.set(type, value); this.types = [...data.keys()]; }, getData: type => data.get(type) };
    function event(kind, nodeId = 'fetch') {
        const node = { dataset: { nodeId } };
        const port = { closest: selector => selector === '.flow-node' ? node : null };
        return {
            button: 0, pointerId: 1, dataTransfer: transfer,
            target: { closest: selector => selector === '.flow-node' ? node
                : selector === `.${kind}-port` || selector === '.node-port' && ['input', 'output'].includes(kind) ? port : null,
                matches: selector => kind === 'node' && selector === '.flow-node' },
            preventDefault() { this.prevented = true; }, stopPropagation() { this.stopped = true; }
        };
    }
    return { events, calls, svg, event, context, transfer };
}

test('native output drag carries its node id and input drop sends exactly one connection', async () => {
    const app = setup();
    const output = app.event('output');
    app.events.pointerdown(output);
    assert.equal(output.prevented, undefined);
    assert.equal(app.svg.__flowCanvas.state.drag, null);
    app.events.dragstart(output);
    assert.equal(app.transfer.effectAllowed, 'link');
    const input = app.event('input', 'parser');
    app.events.dragover(input);
    assert.equal(input.prevented, true);
    assert.equal(app.transfer.dropEffect, 'link');
    await app.events.drop(input);
    assert.equal(input.stopped, true);
    assert.deepEqual(app.calls, [['OnSourceConnectionDrop', 'fetch', 'parser']]);
});

test('file drops, unrelated drags and drops outside input handles are not intercepted', () => {
    const app = setup();
    app.transfer.types = ['Files'];
    const input = app.event('input');
    app.events.dragover(input); app.events.drop(input);
    assert.equal(input.prevented, undefined);
    app.events.dragstart(app.event('output'));
    const body = app.event('node');
    app.events.dragover(body); app.events.drop(body);
    assert.equal(body.prevented, undefined);
    assert.deepEqual(app.calls, []);
});

test('keyboard selection prevents scroll only for Enter/Space on nodes and all listeners are disposed', () => {
    const app = setup();
    for (const key of ['Enter', ' ']) {
        const node = { ...app.event('node'), key };
        app.events.keydown(node);
        assert.equal(node.prevented, true);
    }
    const tab = { ...app.event('node'), key: 'Tab' };
    const button = { ...app.event('input'), key: ' ' };
    app.events.keydown(tab); app.events.keydown(button);
    assert.equal(tab.prevented, undefined);
    assert.equal(button.prevented, undefined);
    app.context.dispose(app.svg);
    assert.deepEqual(Object.keys(app.events), []);
});
