export function initialize(dock, separator, dotNetRef) {
    if (separator.__dockSplitter) return;
    let pointerId = null;
    let value = Number(separator.getAttribute('aria-valuenow'));
    const apply = next => {
        value = Math.max(20, Math.min(80, next));
        dock.style.setProperty('--toolbox-share', `${value}fr`);
        dock.style.setProperty('--overview-share', `${100 - value}fr`);
        separator.setAttribute('aria-valuenow', String(value));
    };
    const move = event => {
        if (event.pointerId !== pointerId) return;
        const bounds = dock.getBoundingClientRect();
        const usable = bounds.height - separator.offsetHeight;
        if (usable > 0) apply((event.clientY - bounds.top - separator.offsetHeight / 2) / usable * 100);
    };
    const down = event => {
        if (event.button !== 0 || pointerId !== null) return;
        event.preventDefault();
        value = Number(separator.getAttribute('aria-valuenow'));
        pointerId = event.pointerId;
        separator.focus();
        separator.setPointerCapture(pointerId);
    };
    const end = event => {
        if (event.pointerId !== pointerId) return;
        const released = pointerId;
        pointerId = null;
        if (separator.hasPointerCapture(released)) separator.releasePointerCapture(released);
        dotNetRef.invokeMethodAsync('OnDockSplitChanged', value);
    };
    const key = event => {
        if (['ArrowUp', 'ArrowDown', 'Home', 'End'].includes(event.key)) event.preventDefault();
    };
    separator.addEventListener('pointerdown', down);
    separator.addEventListener('pointermove', move);
    separator.addEventListener('pointerup', end);
    separator.addEventListener('pointercancel', end);
    separator.addEventListener('lostpointercapture', end);
    separator.addEventListener('keydown', key);
    separator.__dockSplitter = { down, move, end, key };
}

export function dispose(separator) {
    const handlers = separator.__dockSplitter;
    if (!handlers) return;
    separator.removeEventListener('pointerdown', handlers.down);
    separator.removeEventListener('pointermove', handlers.move);
    separator.removeEventListener('pointerup', handlers.end);
    separator.removeEventListener('pointercancel', handlers.end);
    separator.removeEventListener('lostpointercapture', handlers.end);
    separator.removeEventListener('keydown', handlers.key);
    delete separator.__dockSplitter;
}
