import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";

const source = await readFile(new URL("../Mulse.Ui/wwwroot/js/reference-input.js", import.meta.url), "utf8");
const { completions, attach } = await import(`data:text/javascript;base64,${Buffer.from(source).toString("base64")}`);
const names = ["db-host", "db-name", "credential"];

test("opening braces suggest both namespaces, never values", () => {
    assert.deepEqual(completions("{{", 2, names).map(x => x.token), [
        "{{config:db-host}}", "{{secret:db-host}}", "{{config:db-name}}", "{{secret:db-name}}", "{{config:credential}}", "{{secret:credential}}"
    ]);
    assert.deepEqual(completions("{", 1, names), []);
    assert.deepEqual(completions("literal", 7, names), []);
});

test("filters namespace and name case-insensitively", () => {
    assert.deepEqual(completions("{{SECRET:DB", 11, names).map(x => x.token), ["{{secret:db-host}}", "{{secret:db-name}}"]);
    assert.deepEqual(completions("{{credential", 12, names).map(x => x.token), ["{{config:credential}}", "{{secret:credential}}"]);
});

test("inserts at caret without losing surrounding text or duplicating closing braces", () => {
    const value = "Server={{config:db}};other={{secret:credential}};";
    const caret = value.indexOf("}}");
    const choice = completions(value, caret, names)[0];
    assert.equal(value.slice(0, choice.start) + choice.token + value.slice(choice.end), "Server={{config:db-host}};other={{secret:credential}};");
    const unfinished = "before {{sec after";
    const option = completions(unfinished, 12, names)[0];
    assert.equal(unfinished.slice(0, option.start) + option.token + unfinished.slice(option.end), "before {{secret:db-host}} after");
});

test("closed, malformed and multiline fragments do not reopen; adjacent tokens do", () => {
    assert.deepEqual(completions("{{config:db-host}}", 18, names), []);
    assert.deepEqual(completions("{{config:\n", 10, names), []);
    assert.deepEqual(completions("{{config:{", 10, names), []);
    assert.equal(completions("{{config:db-host}}{{", 20, names).length, 6);
    assert.equal(completions("first line\n{{", 13, names).length, 6);
    assert.deepEqual(completions("{{", 2, ["bad{key", "bad\nkey", ""]), []);
});

class Element {
    constructor() { this.attributes = {}; this.listeners = new Map(); this.children = []; this.hidden = true; }
    setAttribute(key, value) { this.attributes[key] = value; }
    removeAttribute(key) { delete this.attributes[key]; }
    addEventListener(name, callback) { this.listeners.set(name, callback); }
    removeEventListener(name) { this.listeners.delete(name); }
    querySelectorAll() { return this.children; }
    replaceChildren() { this.children = []; }
    append(child) { this.children.push(child); }
    scrollIntoView() { }
}

function editor() {
    const popup = new Element();
    const root = new Element();
    root.querySelector = () => popup;
    root.contains = value => value === input;
    const input = new Element();
    input.value = "prefix {{";
    input.selectionStart = input.value.length;
    input.matches = selector => selector === "[data-reference-editor]";
    input.focus = () => { };
    input.setSelectionRange = start => input.selectionStart = start;
    input.dispatchEvent = event => root.listeners.get(event.type)?.({ target: input });
    globalThis.document = { createElement: () => new Element() };
    const controller = attach(root);
    controller.updateReferences(names);
    const event = key => ({ target: input, key, preventDefault() { this.prevented = true; }, stopPropagation() { } });
    return { root, popup, input, controller, event };
}

test("keyboard navigation accepts selection and propagates the actual input value", () => {
    const { root, popup, input, controller, event } = editor();
    root.listeners.get("input")(event());
    assert.equal(popup.hidden, false);
    assert.equal(input.attributes["aria-expanded"], "true");
    root.listeners.get("keydown")(event("ArrowDown"));
    const enter = event("Enter");
    root.listeners.get("keydown")(enter);
    assert.equal(enter.prevented, true);
    assert.equal(input.value, "prefix {{secret:db-host}}");
    assert.equal(input.selectionStart, input.value.length);
    assert.equal(popup.hidden, true);
    controller.dispose();
    assert.equal(root.listeners.size, 0);
});

test("pointer selection, Escape and blur dismiss correctly", () => {
    const { root, popup, input, controller, event } = editor();
    root.listeners.get("input")(event());
    root.listeners.get("keydown")(event("Escape"));
    assert.equal(popup.hidden, true);
    root.listeners.get("click")(event());
    root.listeners.get("focusout")({ relatedTarget: null });
    assert.equal(popup.hidden, true);
    root.listeners.get("focusin")(event());
    popup.children[2].listeners.get("pointerdown")({ preventDefault() { } });
    assert.equal(input.value, "prefix {{config:db-name}}");
    assert.equal(popup.hidden, true);
    controller.dispose();
});
