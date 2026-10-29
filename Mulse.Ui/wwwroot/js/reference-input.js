export function completions(value, caret, references) {
    const start = value.lastIndexOf("{{", caret - 1);
    if (start < 0 || caret < start + 2) return [];
    const fragment = value.slice(start + 2, caret);
    if (/[{}\r\n]/.test(fragment)) return [];
    const closing = value.indexOf("}}", caret);
    const end = closing >= 0 && !/[{}\r\n]/.test(value.slice(caret, closing)) ? closing + 2 : caret;
    const query = fragment.toLowerCase();
    return references.filter(name => name.length && !/[{}\r\n]/.test(name))
        .flatMap(name => ["config", "secret"].map(kind => `${kind}:${name}`))
        .filter(token => token.toLowerCase().includes(query))
        .slice(0, 30)
        .map(token => ({ token: `{{${token}}}`, start, end }));
}

let nextId = 0;

export function attach(root) {
    const popup = root.querySelector("[data-reference-popup]");
    popup.id = `reference-options-${++nextId}`;
    popup.setAttribute("role", "listbox");
    let references = [];
    let input = null;
    let options = [];
    let selected = 0;

    function close() {
        popup.hidden = true;
        if (input) {
            input.setAttribute("aria-expanded", "false");
            input.removeAttribute("aria-activedescendant");
        }
    }

    function highlight() {
        [...popup.querySelectorAll("button")].forEach((button, index) => {
            button.setAttribute("aria-selected", index === selected ? "true" : "false");
        });
        const active = popup.querySelectorAll("button")[selected];
        if (active) {
            input.setAttribute("aria-activedescendant", active.id);
            active.scrollIntoView({ block: "nearest" });
        }
    }

    function choose(index) {
        const option = options[index];
        if (!option || !input) return;
        input.value = input.value.slice(0, option.start) + option.token + input.value.slice(option.end);
        const caret = option.start + option.token.length;
        input.focus();
        input.setSelectionRange(caret, caret);
        input.dispatchEvent(new Event("input", { bubbles: true }));
        close();
    }

    function show(event) {
        if (!event.target.matches("[data-reference-editor]")) return;
        input = event.target;
        options = completions(input.value, input.selectionStart ?? input.value.length, references);
        selected = 0;
        popup.replaceChildren();
        if (!options.length) { close(); return; }
        input.setAttribute("aria-controls", popup.id);
        input.setAttribute("aria-expanded", "true");
        options.forEach((option, index) => {
            const button = document.createElement("button");
            button.type = "button";
            button.id = `${popup.id}-${index}`;
            button.setAttribute("role", "option");
            button.tabIndex = -1;
            button.textContent = option.token;
            button.addEventListener("pointerdown", e => { e.preventDefault(); choose(index); });
            popup.append(button);
        });
        popup.hidden = false;
        highlight();
    }

    function keydown(event) {
        if (popup.hidden || event.target !== input) return;
        if (event.key === "Escape") { event.preventDefault(); event.stopPropagation(); close(); }
        else if (event.key === "ArrowDown" || event.key === "ArrowUp") {
            event.preventDefault();
            event.stopPropagation();
            selected = (selected + (event.key === "ArrowDown" ? 1 : -1) + options.length) % options.length;
            highlight();
        } else if (event.key === "Enter" || event.key === "Tab") {
            event.preventDefault();
            event.stopPropagation();
            choose(selected);
        }
    }

    function focusout(event) {
        if (!root.contains(event.relatedTarget)) close();
    }

    function keyup(event) {
        if (["ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key)) show(event);
    }

    root.addEventListener("input", show);
    root.addEventListener("focusin", show);
    root.addEventListener("click", show);
    root.addEventListener("keydown", keydown);
    root.addEventListener("keyup", keyup);
    root.addEventListener("focusout", focusout);
    return {
        updateReferences(names) { references = names; },
        dispose() {
            close();
            root.removeEventListener("input", show);
            root.removeEventListener("focusin", show);
            root.removeEventListener("click", show);
            root.removeEventListener("keydown", keydown);
            root.removeEventListener("keyup", keyup);
            root.removeEventListener("focusout", focusout);
        }
    };
}
