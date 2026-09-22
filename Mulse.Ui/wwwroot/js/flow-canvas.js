const NODE_WIDTH = 210;
const NODE_HEIGHT = 96;

function readTranslate(group) {
    const transform = group.getAttribute('transform') || '';
    const match = /translate\(\s*(-?[\d.]+)[ ,]+(-?[\d.]+)\s*\)/.exec(transform);
    if (!match) {
        return { x: 0, y: 0 };
    }

    return { x: parseFloat(match[1]), y: parseFloat(match[2]) };
}

function toSvgPoint(svg, event) {
    const matrix = svg.getScreenCTM();
    if (!matrix) {
        return { x: event.clientX, y: event.clientY };
    }

    const point = new DOMPoint(event.clientX, event.clientY).matrixTransform(matrix.inverse());
    return { x: point.x, y: point.y };
}

function nodePosition(svg, nodeId) {
    const group = svg.querySelector(`.flow-node[data-node-id="${nodeId}"]`);
    return group ? readTranslate(group) : null;
}

function updateEdges(svg, nodeId) {
    const edges = svg.querySelectorAll(`.flow-edge[data-edge-from="${nodeId}"], .flow-edge[data-edge-to="${nodeId}"]`);
    edges.forEach(edge => {
        const from = nodePosition(svg, edge.dataset.edgeFrom);
        const to = nodePosition(svg, edge.dataset.edgeTo);
        if (!from || !to) {
            return;
        }

        if (edge.dataset.edgeKind === 'dependency') {
            const x1 = from.x + (NODE_WIDTH / 2);
            const y1 = from.y + NODE_HEIGHT;
            const x2 = to.x + (NODE_WIDTH / 2);
            const y2 = to.y;
            const control = (y1 + y2) / 2;
            edge.setAttribute('d', `M ${x1} ${y1} C ${x1} ${control}, ${x2} ${control}, ${x2} ${y2}`);
            return;
        }

        const x1 = from.x + NODE_WIDTH;
        const y1 = from.y + (NODE_HEIGHT / 2);
        const x2 = to.x;
        const y2 = to.y + (NODE_HEIGHT / 2);
        const control = (x1 + x2) / 2;
        edge.setAttribute('d', `M ${x1} ${y1} C ${control} ${y1}, ${control} ${y2}, ${x2} ${y2}`);
    });
}

function suppressNextClick() {
    const handler = event => {
        event.stopPropagation();
        event.preventDefault();
        window.removeEventListener('click', handler, true);
    };

    window.addEventListener('click', handler, true);
}

export function initialize(svg, dotNetRef) {
    if (!svg || svg.__flowCanvas) {
        return;
    }

    const state = { drag: null };

    const onPointerDown = event => {
        if (event.button !== 0) {
            return;
        }

        const group = event.target.closest('.flow-node');
        if (!group) {
            return;
        }

        const pointer = toSvgPoint(svg, event);
        const origin = readTranslate(group);
        state.drag = {
            group,
            id: group.dataset.nodeId,
            pointerId: event.pointerId,
            startX: pointer.x,
            startY: pointer.y,
            originX: origin.x,
            originY: origin.y,
            x: origin.x,
            y: origin.y,
            moved: false
        };

        group.classList.add('dragging');
        svg.setPointerCapture(event.pointerId);
        event.preventDefault();
    };

    const onPointerMove = event => {
        const drag = state.drag;
        if (!drag || drag.pointerId !== event.pointerId) {
            return;
        }

        const pointer = toSvgPoint(svg, event);
        const x = Math.max(0, drag.originX + (pointer.x - drag.startX));
        const y = Math.max(0, drag.originY + (pointer.y - drag.startY));
        drag.x = x;
        drag.y = y;
        if (Math.abs(x - drag.originX) > 2 || Math.abs(y - drag.originY) > 2) {
            drag.moved = true;
        }

        drag.group.setAttribute('transform', `translate(${x}, ${y})`);
        updateEdges(svg, drag.id);
        event.preventDefault();
    };

    const endDrag = async event => {
        const drag = state.drag;
        if (!drag || drag.pointerId !== event.pointerId) {
            return;
        }

        state.drag = null;
        drag.group.classList.remove('dragging');
        if (svg.hasPointerCapture(event.pointerId)) {
            svg.releasePointerCapture(event.pointerId);
        }

        if (!drag.moved) {
            return;
        }

        suppressNextClick();
        await dotNetRef.invokeMethodAsync('OnNodeMoved', drag.id, Math.round(drag.x), Math.round(drag.y));
    };

    svg.addEventListener('pointerdown', onPointerDown);
    svg.addEventListener('pointermove', onPointerMove);
    svg.addEventListener('pointerup', endDrag);
    svg.addEventListener('pointercancel', endDrag);

    svg.__flowCanvas = { onPointerDown, onPointerMove, endDrag };
}

export function dispose(svg) {
    if (!svg || !svg.__flowCanvas) {
        return;
    }

    const handlers = svg.__flowCanvas;
    svg.removeEventListener('pointerdown', handlers.onPointerDown);
    svg.removeEventListener('pointermove', handlers.onPointerMove);
    svg.removeEventListener('pointerup', handlers.endDrag);
    svg.removeEventListener('pointercancel', handlers.endDrag);
    delete svg.__flowCanvas;
}
