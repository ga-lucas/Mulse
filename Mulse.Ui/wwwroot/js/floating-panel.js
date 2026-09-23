// Drag-to-reposition behavior for FloatingPanel.razor: pointerdown on the header starts a drag,
// pointermove repositions the panel via plain left/top styling, and pointerup reports the final
// position back to the .NET component once (not per-frame) so Blazor's own render stays in sync.
let zCounter = 100;

function bringToFront(panelEl) {
    zCounter += 1;
    panelEl.style.zIndex = String(zCounter);
}

export function initialize(panelEl, headerEl, dotNetRef) {
    if (!panelEl || panelEl.__floatingPanel) {
        return;
    }

    const state = { drag: null };

    const onHeaderPointerDown = event => {
        bringToFront(panelEl);

        if (event.button !== 0 || event.target.closest('button, input, select, textarea, a')) {
            return;
        }

        state.drag = {
            pointerId: event.pointerId,
            startX: event.clientX,
            startY: event.clientY,
            originLeft: panelEl.offsetLeft,
            originTop: panelEl.offsetTop,
            moved: false
        };
        headerEl.setPointerCapture(event.pointerId);
        event.preventDefault();
    };

    const onHeaderPointerMove = event => {
        const drag = state.drag;
        if (!drag || drag.pointerId !== event.pointerId) {
            return;
        }

        const dx = event.clientX - drag.startX;
        const dy = event.clientY - drag.startY;
        if (Math.abs(dx) > 2 || Math.abs(dy) > 2) {
            drag.moved = true;
        }

        const x = Math.max(0, drag.originLeft + dx);
        const y = Math.max(0, drag.originTop + dy);
        panelEl.style.left = `${x}px`;
        panelEl.style.top = `${y}px`;
    };

    const endDrag = async event => {
        const drag = state.drag;
        if (!drag || drag.pointerId !== event.pointerId) {
            return;
        }

        state.drag = null;
        if (headerEl.hasPointerCapture(event.pointerId)) {
            headerEl.releasePointerCapture(event.pointerId);
        }

        if (!drag.moved) {
            return;
        }

        await dotNetRef.invokeMethodAsync('OnMoved', panelEl.offsetLeft, panelEl.offsetTop);
    };

    const onPanelPointerDown = () => bringToFront(panelEl);

    headerEl.addEventListener('pointerdown', onHeaderPointerDown);
    headerEl.addEventListener('pointermove', onHeaderPointerMove);
    headerEl.addEventListener('pointerup', endDrag);
    headerEl.addEventListener('pointercancel', endDrag);
    panelEl.addEventListener('pointerdown', onPanelPointerDown, true);

    panelEl.__floatingPanel = { onHeaderPointerDown, onHeaderPointerMove, endDrag, onPanelPointerDown, headerEl };
}

export function dispose(panelEl) {
    if (!panelEl || !panelEl.__floatingPanel) {
        return;
    }

    const handlers = panelEl.__floatingPanel;
    handlers.headerEl.removeEventListener('pointerdown', handlers.onHeaderPointerDown);
    handlers.headerEl.removeEventListener('pointermove', handlers.onHeaderPointerMove);
    handlers.headerEl.removeEventListener('pointerup', handlers.endDrag);
    handlers.headerEl.removeEventListener('pointercancel', handlers.endDrag);
    panelEl.removeEventListener('pointerdown', handlers.onPanelPointerDown, true);
    delete panelEl.__floatingPanel;
}
