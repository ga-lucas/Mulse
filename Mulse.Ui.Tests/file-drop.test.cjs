const { test } = require('node:test');
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const source = readFileSync(path.join(__dirname, '..', 'Mulse.Ui', 'wwwroot', 'js', 'flow-canvas.js'), 'utf8');

function setup() {
    const events = {};
    const classes = new Set();
    const calls = [];
    const changes = [];
    const surface = {
        addEventListener: (name, callback) => { events[name] = callback; },
        removeEventListener: (name, callback) => { if (events[name] === callback) delete events[name]; },
        contains: target => target === surface
    };
    const svg = {
        parentElement: surface,
        classList: {
            toggle: (name, enabled) => enabled ? classes.add(name) : classes.delete(name),
            remove: name => classes.delete(name)
        }
    };
    const input = { disabled: false, dispatchEvent: event => changes.push(event.type) };
    const context = vm.createContext({ Event: class { constructor(type) { this.type = type; } } });
    vm.runInContext(source.replaceAll('export function ', 'function '), context);
    const initialize = () => context.initializeFileDrop(svg, input, { invokeMethodAsync: (...args) => { calls.push(args); } });
    initialize();
    const event = (files = [{ name: 'orders.xlsx', size: 1024 }], types = ['Files']) => ({
        dataTransfer: { files, types }, prevented: false, stopped: false,
        preventDefault() { this.prevented = true; }, stopPropagation() { this.stopped = true; }
    });
    return { events, classes, calls, changes, input, surface, initialize, event, dispose: () => context.disposeFileDrop(svg) };
}

test('native canvas file drop streams via InputFile and prevents navigation', () => {
    const app = setup();
    const event = app.event();
    app.events.dragover(event);
    assert.equal(event.prevented, true);
    assert.equal(event.dataTransfer.dropEffect, 'copy');
    assert.equal(app.classes.has('drop-target'), true);
    app.events.drop(event);
    assert.equal(event.stopped, true);
    assert.equal(app.input.files, event.dataTransfer.files);
    assert.deepEqual(app.changes, ['change']);
    assert.deepEqual(app.calls, []);
    assert.equal(app.classes.size, 0);
});

test('non-file drags do not interfere with toolbox or text gestures', () => {
    const app = setup();
    const event = app.event([], ['text/plain']);
    app.events.dragover(event);
    app.events.drop(event);
    assert.equal(event.prevented, false);
    assert.deepEqual(app.changes, []);
});

test('busy input blocks repeat uploads and clears hover on leaving canvas', () => {
    const app = setup();
    app.events.dragover(app.event());
    app.events.dragleave({ relatedTarget: app.surface });
    assert.equal(app.classes.has('drop-target'), true);
    app.events.dragleave({ relatedTarget: null });
    assert.equal(app.classes.size, 0);
    app.input.disabled = true;
    const event = app.event();
    app.events.dragover(event);
    assert.equal(event.dataTransfer.dropEffect, 'none');
    app.events.drop(event);
    assert.equal(event.prevented, true);
    assert.deepEqual(app.changes, []);
});

test('oversize or too many files report an error without uploading', () => {
    const app = setup();
    app.events.drop(app.event([{ name: 'large.xlsx', size: 10 * 1024 * 1024 + 1 }]));
    app.events.drop(app.event(Array.from({ length: 11 }, () => ({ size: 1 }))));
    assert.equal(app.calls.length, 2);
    assert.equal(app.calls[0][0], 'OnFileDropError');
    assert.deepEqual(app.changes, []);
});

test('drop initialization is idempotent and listeners are disposed', () => {
    const app = setup();
    const handler = app.events.drop;
    app.initialize();
    assert.equal(app.events.drop, handler);
    app.dispose();
    assert.deepEqual(Object.keys(app.events), []);
    app.dispose();
});
