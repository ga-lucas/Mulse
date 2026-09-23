// Flow canvas interactions: node dragging, canvas pan/zoom (via live SVG viewBox manipulation), and
// toolbox drag-to-add. The SVG's viewBox IS the pan/zoom "camera": panning and zooming mutate it
// directly for a responsive feel, and only report back to Blazor once per gesture (mirroring how node
// drags already only call back on release) so a mid-gesture Blazor re-render never causes a visual jump.
const NODE_WIDTH = 210;
const NODE_HEIGHT = 96;
const MIN_ZOOM_RATIO = 0.15;
const MAX_ZOOM_RATIO = 4;

let viewSyncTimer = null;

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

function scheduleViewSync(svg, dotNetRef) {
    clearTimeout(viewSyncTimer);
    viewSyncTimer = setTimeout(() => {
        const vb = svg.viewBox.baseVal;
        dotNetRef.invokeMethodAsync('OnViewChanged', vb.x, vb.y, vb.width, vb.height);
    }, 150);
}

function applyZoom(svg, factor, focusClientX, focusClientY, state) {
    const vb = svg.viewBox.baseVal;
    const focusPoint = toSvgPoint(svg, { clientX: focusClientX, clientY: focusClientY });
    const naturalWidth = state?.natural?.w ?? vb.width;
    const minWidth = naturalWidth * MIN_ZOOM_RATIO;
    const maxWidth = naturalWidth * MAX_ZOOM_RATIO;
    const newWidth = Math.min(maxWidth, Math.max(minWidth, vb.width / factor));
    const scale = newWidth / vb.width;
    const newHeight = vb.height * scale;

    vb.x = focusPoint.x - ((focusPoint.x - vb.x) * scale);
    vb.y = focusPoint.y - ((focusPoint.y - vb.y) * scale);
    vb.width = newWidth;
    vb.height = newHeight;
}

export function initialize(svg, dotNetRef) {
    if (!svg || svg.__flowCanvas) {
        return;
    }

    const initialViewBox = svg.viewBox.baseVal;
    const state = {
        drag: null,
        natural: { w: initialViewBox.width, h: initialViewBox.height }
    };

    const onPointerDown = event => {
        if (event.button !== 0) {
            return;
        }

        const group = event.target.closest('.flow-node');
        if (group) {
            const pointer = toSvgPoint(svg, event);
            const origin = readTranslate(group);
            state.drag = {
                kind: 'node',
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
            return;
        }

        const vb = svg.viewBox.baseVal;
        const rect = svg.getBoundingClientRect();
        state.drag = {
            kind: 'pan',
            pointerId: event.pointerId,
            startClientX: event.clientX,
            startClientY: event.clientY,
            originX: vb.x,
            originY: vb.y,
            scaleX: vb.width / rect.width,
            scaleY: vb.height / rect.height,
            moved: false
        };
        svg.classList.add('panning');
        svg.setPointerCapture(event.pointerId);
        event.preventDefault();
    };

    const onPointerMove = event => {
        const drag = state.drag;
        if (!drag || drag.pointerId !== event.pointerId) {
            return;
        }

        if (drag.kind === 'node') {
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
        } else {
            const dxScreen = event.clientX - drag.startClientX;
            const dyScreen = event.clientY - drag.startClientY;
            const vb = svg.viewBox.baseVal;
            vb.x = drag.originX - (dxScreen * drag.scaleX);
            vb.y = drag.originY - (dyScreen * drag.scaleY);
            if (Math.abs(dxScreen) > 2 || Math.abs(dyScreen) > 2) {
                drag.moved = true;
            }
        }

        event.preventDefault();
    };

    const endDrag = async event => {
        const drag = state.drag;
        if (!drag || drag.pointerId !== event.pointerId) {
            return;
        }

        state.drag = null;

        if (drag.kind === 'node') {
            drag.group.classList.remove('dragging');
            if (svg.hasPointerCapture(event.pointerId)) {
                svg.releasePointerCapture(event.pointerId);
            }

            if (!drag.moved) {
                return;
            }

            suppressNextClick();
            await dotNetRef.invokeMethodAsync('OnNodeMoved', drag.id, Math.round(drag.x), Math.round(drag.y));
        } else {
            svg.classList.remove('panning');
            if (svg.hasPointerCapture(event.pointerId)) {
                svg.releasePointerCapture(event.pointerId);
            }

            if (drag.moved) {
                suppressNextClick();
                scheduleViewSync(svg, dotNetRef);
            }
        }
    };

    const onWheel = event => {
        event.preventDefault();
        const factor = Math.exp(-event.deltaY * 0.0015);
        applyZoom(svg, factor, event.clientX, event.clientY, state);
        scheduleViewSync(svg, dotNetRef);
    };

    svg.addEventListener('pointerdown', onPointerDown);
    svg.addEventListener('pointermove', onPointerMove);
    svg.addEventListener('pointerup', endDrag);
    svg.addEventListener('pointercancel', endDrag);
    svg.addEventListener('wheel', onWheel, { passive: false });

    svg.__flowCanvas = { onPointerDown, onPointerMove, endDrag, onWheel, state };
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
    svg.removeEventListener('wheel', handlers.onWheel);
    delete svg.__flowCanvas;
}

/// Zooms centered on the canvas' current viewport center (used by toolbar +/- buttons).
export function zoomBy(svg, factor, dotNetRef) {
    if (!svg) {
        return;
    }

    const rect = svg.getBoundingClientRect();
    const state = svg.__flowCanvas?.state;
    applyZoom(svg, factor, rect.left + (rect.width / 2), rect.top + (rect.height / 2), state);

    if (dotNetRef) {
        const vb = svg.viewBox.baseVal;
        dotNetRef.invokeMethodAsync('OnViewChanged', vb.x, vb.y, vb.width, vb.height);
    }
}

// Custom pointer-based drag-and-drop from the toolbox onto the canvas (used instead of native HTML5
// DnD so the same pointer-capture approach used everywhere else in this file applies here too).
export function initializeToolbox(toolboxEl, canvasEl, dotNetRef) {
    if (!toolboxEl || toolboxEl.__toolboxDrag) {
        return;
    }

    let ghost = null;
    let dragging = null;

    const positionGhost = (x, y) => {
        if (ghost) {
            ghost.style.left = `${x + 14}px`;
            ghost.style.top = `${y + 14}px`;
        }
    };

    const onPointerDown = event => {
        if (event.button !== 0) {
            return;
        }

        const item = event.target.closest('[data-node-type]');
        if (!item) {
            return;
        }

        dragging = {
            nodeType: item.dataset.nodeType,
            pointerId: event.pointerId,
            startX: event.clientX,
            startY: event.clientY,
            moved: false
        };
        ghost = document.createElement('div');
        ghost.className = 'toolbox-ghost';
        ghost.textContent = item.dataset.nodeLabel || item.dataset.nodeType;
        document.body.appendChild(ghost);
        positionGhost(event.clientX, event.clientY);
        item.setPointerCapture(event.pointerId);
        event.preventDefault();
    };

    const onPointerMove = event => {
        if (!dragging || dragging.pointerId !== event.pointerId) {
            return;
        }

        if (Math.abs(event.clientX - dragging.startX) > 4 || Math.abs(event.clientY - dragging.startY) > 4) {
            dragging.moved = true;
        }

        positionGhost(event.clientX, event.clientY);
        const rect = canvasEl.getBoundingClientRect();
        const over = dragging.moved
            && event.clientX >= rect.left && event.clientX <= rect.right
            && event.clientY >= rect.top && event.clientY <= rect.bottom;
        canvasEl.classList.toggle('drop-target', over);
    };

    // Pointer capture keeps every pointer/click event targeted at the toolbox button regardless of
    // where the pointer is released, so a plain click (no movement) is left entirely to the button's
    // own @onclick handler; only an actual drag (movement past the threshold) triggers a canvas drop,
    // and that drop's resulting synthetic click is swallowed so the click handler doesn't also fire.
    const endDrag = async event => {
        if (!dragging || dragging.pointerId !== event.pointerId) {
            return;
        }

        const { nodeType, moved } = dragging;
        dragging = null;
        if (ghost) {
            ghost.remove();
            ghost = null;
        }
        canvasEl.classList.remove('drop-target');

        if (!moved) {
            return;
        }

        const rect = canvasEl.getBoundingClientRect();
        if (event.clientX >= rect.left && event.clientX <= rect.right
            && event.clientY >= rect.top && event.clientY <= rect.bottom) {
            suppressNextClick();
            const point = toSvgPoint(canvasEl, event);
            await dotNetRef.invokeMethodAsync('OnToolboxDrop', nodeType, point.x, point.y);
        } else {
            suppressNextClick();
        }
    };

    toolboxEl.addEventListener('pointerdown', onPointerDown);
    toolboxEl.addEventListener('pointermove', onPointerMove);
    toolboxEl.addEventListener('pointerup', endDrag);
    toolboxEl.addEventListener('pointercancel', endDrag);

    toolboxEl.__toolboxDrag = { onPointerDown, onPointerMove, endDrag };
}

export function disposeToolbox(toolboxEl) {
    if (!toolboxEl || !toolboxEl.__toolboxDrag) {
        return;
    }

    const handlers = toolboxEl.__toolboxDrag;
    toolboxEl.removeEventListener('pointerdown', handlers.onPointerDown);
    toolboxEl.removeEventListener('pointermove', handlers.onPointerMove);
    toolboxEl.removeEventListener('pointerup', handlers.endDrag);
    toolboxEl.removeEventListener('pointercancel', handlers.endDrag);
    delete toolboxEl.__toolboxDrag;
}
