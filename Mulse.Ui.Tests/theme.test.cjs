const { test } = require('node:test');
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const source = readFileSync(path.join(__dirname, '..', 'Mulse.Ui', 'wwwroot', 'js', 'theme.js'), 'utf8');

function setup({ saved = null, dark = false, blocked = false } = {}) {
    const events = {};
    const warnings = [];
    const attributes = {};
    const storage = new Map(saved === null ? [] : [['mulse.theme', saved]]);
    class Element {
        closest() { return this.toggle ? this : null; }
    }
    const button = new Element();
    button.toggle = true;
    button.setAttribute = (key, value) => { attributes[key] = value; };
    const systemTheme = {
        matches: dark,
        addEventListener: (name, callback) => { events[`system:${name}`] = callback; }
    };
    const document = {
        documentElement: { dataset: {} },
        querySelectorAll: () => [button],
        addEventListener: (name, callback) => { events[name] = callback; }
    };
    const window = {
        matchMedia: () => systemTheme,
        localStorage: {
            getItem(key) {
                if (blocked) throw new Error('Storage unavailable');
                return storage.get(key) ?? null;
            },
            setItem(key, value) {
                if (blocked) throw new Error('Storage unavailable');
                storage.set(key, value);
            }
        },
        addEventListener: (name, callback) => { events[`window:${name}`] = callback; }
    };
    vm.runInNewContext(source, { window, document, Element, console: { warn: (...args) => warnings.push(args) } });
    return {
        theme: () => document.documentElement.dataset.theme,
        pressed: () => attributes['aria-pressed'],
        click: () => events.click({ target: button }),
        system(dark) { systemTheme.matches = dark; events['system:change'](); },
        remount() { attributes['aria-pressed'] = 'false'; },
        refresh: window.mulseTheme.refresh,
        events, storage, warnings
    };
}

test('refreshes remounted controls after interactive rendering', () => {
    const app = setup({ saved: 'dark' });
    app.remount();
    assert.equal(app.pressed(), 'false');
    app.refresh();
    assert.equal(app.pressed(), 'true');
    assert.equal(app.theme(), 'dark');
});

test('follows system appearance until explicitly selected', () => {
    const app = setup();
    assert.equal(app.theme(), 'light');
    app.system(true);
    assert.equal(app.theme(), 'dark');
    assert.equal(app.pressed(), 'true');
    app.click();
    assert.equal(app.theme(), 'light');
    assert.equal(app.storage.get('mulse.theme'), 'light');
    app.system(true);
    assert.equal(app.theme(), 'light');
});

test('restores both saved themes before page content loads', () => {
    for (const saved of ['light', 'dark']) {
        const app = setup({ saved, dark: saved === 'light' });
        assert.equal(app.theme(), saved);
        app.events.DOMContentLoaded();
        assert.equal(app.pressed(), String(saved === 'dark'));
        app.events.enhancedload();
        assert.equal(app.theme(), saved);
    }
});

test('ignores invalid saved themes and unrelated clicks', () => {
    const app = setup({ saved: 'invalid', dark: true });
    app.events.click({ target: {} });
    assert.equal(app.theme(), 'dark');
    app.system(false);
    assert.equal(app.theme(), 'light');
});

test('toggles both ways and updates accessible pressed state', () => {
    const app = setup();
    app.click();
    assert.equal(app.theme(), 'dark');
    assert.equal(app.pressed(), 'true');
    app.click();
    assert.equal(app.theme(), 'light');
    assert.equal(app.pressed(), 'false');
});

test('keeps theme controls working and reports unavailable storage', () => {
    const app = setup({ blocked: true });
    assert.equal(app.warnings.length, 1);
    app.click();
    assert.equal(app.theme(), 'dark');
    assert.equal(app.warnings.length, 2);
});

test('synchronizes other tabs and returns to the system preference when cleared', () => {
    const app = setup({ saved: 'dark' });
    app.events['window:storage']({ key: 'unrelated', newValue: 'light' });
    assert.equal(app.theme(), 'dark');
    app.events['window:storage']({ key: 'mulse.theme', newValue: 'light' });
    assert.equal(app.theme(), 'light');
    app.events['window:storage']({ key: null, newValue: null });
    app.system(true);
    assert.equal(app.theme(), 'dark');
});
