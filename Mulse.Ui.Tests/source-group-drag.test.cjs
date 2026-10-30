const { test } = require('node:test');
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const source = readFileSync(path.join(__dirname, '..', 'Mulse.Ui', 'wwwroot', 'js', 'flow-canvas.js'), 'utf8');

function element(dataset = {}, attributes = {}) {
    const classes = new Set();
    const captured = new Set();
    return {
        dataset, attributes, classes, captured,
        getAttribute: name => attributes[name],
        setAttribute: (name, value) => { attributes[name] = String(value); },
        classList: { add: name => classes.add(name), remove: name => classes.delete(name) },
        setPointerCapture: id => captured.add(id),
        hasPointerCapture: id => captured.has(id),
        releasePointerCapture: id => captured.delete(id)
    };
}

function setup(scale = 1, origin = { x: 0, y: 0 }) {
    const events = {};
    const clickEvents = {};
    const calls = [];
    const nodes = {
        shared: element({ nodeId: 'shared' }, { transform: 'translate(40, 100)' }),
        first: element({ nodeId: 'first' }, { transform: 'translate(310, 100)' }),
        second: element({ nodeId: 'second' }, { transform: 'translate(310, 300)' }),
        render: element({ nodeId: 'render' }, { transform: 'translate(850, 200)' })
    };
    function lane(members) {
        const result = element({ laneMembers: members });
        result.parts = Object.fromEntries(['body', 'handle', 'label'].map(name => [`.source-lane-${name}`, element()]));
        result.querySelector = selector => result.parts[selector];
        result.parts['.source-lane-handle'].closest = selector => selector === '.source-lane' ? result : null;
        return result;
    }
    const lanes = [lane('shared first'), lane('shared second')];
    const edges = [element({ edgeFrom: 'shared', edgeTo: 'first', edgeKind: 'flow' }),
        element({ edgeFrom: 'first', edgeTo: 'second', edgeKind: 'dependency' }),
        element({ edgeFrom: 'second', edgeTo: 'render', edgeKind: 'flow' })];
    const svg = {
        ...element(),
        viewBox: { baseVal: { x: 0, y: 0, width: 1350, height: 650 } },
        getScreenCTM: () => ({ inverse: () => ({ scale, ...origin }) }),
        getBoundingClientRect: () => ({ left: 0, top: 0, right: 1080, bottom: 520, width: 1080, height: 520 }),
        querySelector: selector => nodes[/data-node-id="([^"]+)"/.exec(selector)?.[1]] ?? null,
        querySelectorAll: selector => selector === '.source-lane' ? lanes
            : edges.filter(edge => selector.includes(`="${edge.dataset.edgeFrom}"`) || selector.includes(`="${edge.dataset.edgeTo}"`)),
        addEventListener: (name, handler) => { events[name] = handler; },
        removeEventListener: name => { delete events[name]; }
    };
    const context = vm.createContext({
        DOMPoint: class {
            constructor(x, y) { this.x = x; this.y = y; }
            matrixTransform(matrix) { return { x: this.x * matrix.scale + matrix.x, y: this.y * matrix.scale + matrix.y }; }
        },
        window: { addEventListener: (name, handler) => { clickEvents[name] = handler; }, removeEventListener: name => { delete clickEvents[name]; } }
    });
    vm.runInContext(source.replaceAll('export function ', 'function '), context);
    context.updateSourceLanes(svg);
    context.initialize(svg, { invokeMethodAsync: (...args) => { calls.push(JSON.parse(JSON.stringify(args))); } });
    const pointer = (x, y, { id = 7, button = 0, target = 'header' } = {}) => ({
        button, pointerId: id, clientX: x, clientY: y, prevented: false,
        target: { closest: selector => {
            if (selector === '.source-lane-handle' && target === 'header') return lanes[0].parts[selector];
            if (selector === '.flow-node' && target === 'node') return nodes.shared;
            if (selector === '.node-remove' && target === 'remove') return {};
            return null;
        } },
        preventDefault() { this.prevented = true; }
    });
    return { events, calls, nodes, lanes, edges, svg, context, pointer, clickEvents };
}

function position(node) { return node.attributes.transform; }

test('header drag moves members once at 80% scale, updates both lanes and all connected edges, and commits atomically', async () => {
    const app = setup(1.25);
    app.events.pointerdown(app.pointer(100, 100));
    assert.equal(app.svg.__flowCanvas.state.drag.kind, 'lane');
    app.events.pointermove(app.pointer(180, 140));
    assert.equal(position(app.nodes.shared), 'translate(140, 150)');
    assert.equal(position(app.nodes.first), 'translate(410, 150)');
    assert.equal(position(app.nodes.second), 'translate(310, 300)');
    assert.equal(app.lanes[0].parts['.source-lane-body'].attributes.x, '122');
    assert.equal(app.lanes[0].parts['.source-lane-handle'].attributes.y, '112');
    assert.equal(app.lanes[1].parts['.source-lane-body'].attributes.x, '122');
    assert.equal(app.lanes[1].parts['.source-lane-body'].attributes.height, '302');
    assert.equal(app.edges[0].attributes.d, 'M 350 198 C 380 198, 380 198, 410 198');
    assert.equal(app.edges[1].attributes.d, 'M 515 246 C 515 273, 415 273, 415 300');
    assert.deepEqual(app.calls, []);
    await app.events.pointerup(app.pointer(180, 140));
    assert.deepEqual(app.calls, [['OnNodesMoved', [{ id: 'shared', x: 140, y: 150 }, { id: 'first', x: 410, y: 150 }]]]);
    assert.equal(app.lanes[0].captured.size, 0);
    assert.equal(app.lanes[0].classes.has('dragging'), false);
    assert.equal(app.svg.__flowCanvas.state.drag, null);
    assert.equal(typeof app.clickEvents.click, 'function');
});

test('groups move past the origin without compressing spacing or clipping headers', async () => {
    const app = setup();
    app.events.pointerdown(app.pointer(100, 100));
    app.events.pointermove(app.pointer(-1000, -1000));
    await app.events.pointerup(app.pointer(-1000, -1000));
    assert.equal(position(app.nodes.shared), 'translate(-1060, -1000)');
    assert.equal(position(app.nodes.first), 'translate(-790, -1000)');
    assert.equal(app.lanes[0].parts['.source-lane-body'].attributes.x, '-1078');
    assert.equal(app.lanes[0].parts['.source-lane-handle'].attributes.y, '-1038');
    assert.equal(app.lanes[0].parts['.source-lane-body'].attributes.width, '516');
    assert.deepEqual(app.calls[0][1], [{ id: 'shared', x: -1060, y: -1000 }, { id: 'first', x: -790, y: -1000 }]);
});

test('individual nodes cross both origin boundaries at zoom without squashing the group', async () => {
    const app = setup(1.25);
    app.events.pointerdown(app.pointer(100, 100, { target: 'node' }));
    app.events.pointermove(app.pointer(-100, -100, { target: 'node' }));
    await app.events.pointerup(app.pointer(-100, -100, { target: 'node' }));
    assert.equal(position(app.nodes.shared), 'translate(-210, -150)');
    assert.equal(app.lanes[0].parts['.source-lane-body'].attributes.x, '-228');
    assert.equal(app.lanes[0].parts['.source-lane-body'].attributes.y, '-188');
    assert.equal(position(app.nodes.first), 'translate(310, 100)');
    assert.deepEqual(app.calls, [['OnNodeMoved', 'shared', -210, -150]]);
});

test('panning reaches negative world coordinates and reports the signed view unchanged', async () => {
    const app = setup();
    let scheduled;
    app.context.setTimeout = callback => { scheduled = callback; };
    app.context.clearTimeout = () => {};
    app.events.pointerdown(app.pointer(100, 100, { target: 'body' }));
    app.events.pointermove(app.pointer(900, 500, { target: 'body' }));
    await app.events.pointerup(app.pointer(900, 500, { target: 'body' }));
    assert.equal(app.svg.viewBox.baseVal.x, -1000);
    assert.equal(app.svg.viewBox.baseVal.y, -500);
    scheduled();
    assert.deepEqual(app.calls, [['OnViewChanged', -1000, -500, 1350, 650]]);
});

test('toolbox drops in a panned zoomed viewport send signed world coordinates', async () => {
    const app = setup(1.25, { x: -2000, y: -1000 });
    const toolboxEvents = {};
    const toolbox = {
        addEventListener: (name, handler) => { toolboxEvents[name] = handler; }
    };
    const item = { dataset: { nodeType: 'source' }, setPointerCapture() {} };
    app.context.document = {
        createElement: () => ({ style: {}, remove() {} }),
        body: { appendChild() {} }
    };
    app.svg.classList.toggle = () => {};
    app.context.initializeToolbox(toolbox, app.svg, { invokeMethodAsync: (...args) => app.calls.push(args) });
    const event = (x, y) => ({
        button: 0, pointerId: 7, clientX: x, clientY: y,
        target: { closest: () => item }, preventDefault() {}
    });
    toolboxEvents.pointerdown(event(10, 10));
    toolboxEvents.pointermove(event(100, 200));
    await toolboxEvents.pointerup(event(100, 200));
    assert.deepEqual(app.calls, [['OnToolboxDrop', 'source', -1875, -750]]);
});

test('a click or sub-threshold jitter does not move members or send a callback', async () => {
    const app = setup();
    app.events.pointerdown(app.pointer(100, 100));
    app.events.pointermove(app.pointer(101, 101));
    await app.events.pointerup(app.pointer(101, 101));
    assert.equal(position(app.nodes.shared), 'translate(40, 100)');
    assert.deepEqual(app.calls, []);
    assert.equal(app.lanes[0].captured.size, 0);
});

test('secondary buttons and remove controls do not start group drag', () => {
    const app = setup();
    for (const options of [{ button: 2 }, { target: 'remove' }]) {
        const event = app.pointer(100, 100, options);
        app.events.pointerdown(event);
        assert.equal(event.prevented, false);
        assert.equal(app.svg.__flowCanvas.state.drag, null);
    }
});

test('another pointer cannot steal or end an active drag', async () => {
    const app = setup();
    app.events.pointerdown(app.pointer(100, 100));
    app.events.pointerdown(app.pointer(400, 400, { id: 8 }));
    app.events.pointermove(app.pointer(200, 200, { id: 8 }));
    await app.events.pointerup(app.pointer(200, 200, { id: 8 }));
    assert.equal(position(app.nodes.shared), 'translate(40, 100)');
    assert.equal(app.svg.__flowCanvas.state.drag.pointerId, 7);
    await app.events.pointerup(app.pointer(100, 100));
    assert.deepEqual(app.calls, []);
});

test('pointer cancellation commits the visible positions once, matching individual-node drag behavior', async () => {
    const app = setup();
    app.events.pointerdown(app.pointer(100, 100));
    app.events.pointermove(app.pointer(120, 130));
    await app.events.pointercancel(app.pointer(120, 130));
    await app.events.pointerup(app.pointer(120, 130));
    assert.equal(app.calls.length, 1);
    assert.equal(position(app.nodes.first), 'translate(330, 130)');
    assert.equal(app.lanes[0].captured.size, 0);
});

test('individual node drags resize every lane containing the node', async () => {
    const app = setup();
    app.events.pointerdown(app.pointer(100, 100, { target: 'node' }));
    app.events.pointermove(app.pointer(120, 140, { target: 'node' }));
    await app.events.pointerup(app.pointer(120, 140, { target: 'node' }));
    assert.equal(app.lanes[0].parts['.source-lane-body'].attributes.x, '42');
    assert.equal(app.lanes[1].parts['.source-lane-body'].attributes.x, '42');
    assert.deepEqual(app.calls, [['OnNodeMoved', 'shared', 60, 140]]);
});

test('lane body still pans the canvas rather than moving its nodes', async () => {
    const app = setup();
    app.events.pointerdown(app.pointer(100, 100, { target: 'body' }));
    assert.equal(app.svg.__flowCanvas.state.drag.kind, 'pan');
    await app.events.pointerup(app.pointer(100, 100, { target: 'body' }));
    assert.deepEqual(app.calls, []);
});

test('disposal releases group capture and restores uncommitted positions and outlines', () => {
    const app = setup();
    app.events.pointerdown(app.pointer(100, 100));
    app.events.pointermove(app.pointer(130, 130));
    app.context.dispose(app.svg);
    assert.equal(position(app.nodes.shared), 'translate(40, 100)');
    assert.equal(app.lanes[0].parts['.source-lane-body'].attributes.x, '22');
    assert.equal(app.lanes[0].captured.size, 0);
    assert.equal(app.lanes[0].classes.has('dragging'), false);
    assert.deepEqual(Object.keys(app.events), []);
    assert.equal(app.svg.__flowCanvas, undefined);
    assert.deepEqual(app.calls, []);
});
