// Fait tourner la page du tableau (media/dataset-editor.js) sur un mini-DOM écrit ici, sans navigateur ni dépendance :
// `node test/webview.test.js` depuis editors/vscode. Il vérifie le rendu, la validation, l'ajout, la duplication et la suppression de lignes,
// et ce que « Appliquer » envoie à l'extension.

"use strict";

const assert = require("assert");
const fs = require("fs");
const path = require("path");
const vm = require("vm");

// ---- un mini-DOM : ce que la page utilise, et rien de plus -------------------------------------------------------------------------

class Text {
    constructor(text) {
        this.text = String(text);
    }
    get textContent() {
        return this.text;
    }
}

class Element {
    constructor(tag) {
        this.tagName = tag.toUpperCase();
        this.attributes = {};
        this.children = [];
        this.listeners = {};
        this.value = "";
        this.disabled = false;
        this.parent = null;
        const element = this;
        this.classList = {
            add: name => { element.className = [...new Set([...element.className.split(" ").filter(Boolean), name])].join(" "); },
            remove: name => { element.className = element.className.split(" ").filter(c => c && c !== name).join(" "); },
            contains: name => element.className.split(" ").includes(name),
        };
        this.className = "";
    }
    setAttribute(key, value) {
        this.attributes[key] = String(value);
        if (key === "value") this.value = String(value);
        if (key === "class") this.className = String(value);
    }
    getAttribute(key) {
        return this.attributes[key];
    }
    append(...nodes) {
        for (const node of nodes) {
            const child = typeof node === "object" && node !== null ? node : new Text(node);
            if (child instanceof Element) child.parent = this;
            this.children.push(child);
        }
    }
    replaceChildren(...nodes) {
        this.children = [];
        this.append(...nodes);
    }
    addEventListener(name, handler) {
        (this.listeners[name] ??= []).push(handler);
    }
    dispatch(name, extra = {}) {
        for (const handler of this.listeners[name] ?? []) handler({ target: this, ...extra });
    }
    focus() {}
    select() {}
    get textContent() {
        return this.children.map(c => c.textContent).join("");
    }
    elements() {
        return this.children.filter(c => c instanceof Element).flatMap(c => [c, ...c.elements()]);
    }
    matches(selector) {
        const parts = selector.match(/^([a-z]*)((?:\.[\w-]+)*)((?:\[[^\]]+\])*)$/);
        if (!parts) throw new Error("sélecteur non géré : " + selector);
        const [, tag, classes, attributes] = parts;
        if (tag && this.tagName !== tag.toUpperCase()) return false;
        for (const name of classes.split(".").filter(Boolean)) if (!this.classList.contains(name)) return false;
        for (const [, key, value] of attributes.matchAll(/\[([\w-]+)="([^"]*)"\]/g)) if (this.attributes[key] !== value) return false;
        return true;
    }
    querySelectorAll(selector) {
        return this.elements().filter(e => e.matches(selector));
    }
    querySelector(selector) {
        return this.querySelectorAll(selector)[0] ?? null;
    }
}

function install(state) {
    const root = new Element("div");
    const posted = [];
    const messageListeners = [];
    const sandbox = {
        document: {
            getElementById: () => root,
            createElement: tag => new Element(tag),
        },
        window: { addEventListener: (_, handler) => messageListeners.push(handler) },
        acquireVsCodeApi: () => ({ postMessage: message => posted.push(message) }),
        RglDatasetModel: require("../dataset-model"),
        JSON,
        Object,
        Array,
        Set,
        RegExp,
    };
    vm.runInNewContext(fs.readFileSync(path.join(__dirname, "..", "media", "dataset-editor.js"), "utf8"), sandbox);
    for (const listener of messageListeners) listener({ data: { type: "init", state } });
    return { root, posted };
}

const freshState = () => ({
    mode: "inline", name: "Moves", mold: "Move", createsMold: false, source: null, file: null, asArray: false, message: null,
    fields: [
        { name: "name", type: "string", required: true, choices: [] },
        { name: "power", type: "int", required: false, choices: [] },
        { name: "element", type: "Element", required: false, choices: [{ label: "Feu", inline: "Element.Feu", json: "\"Feu\"" }] },
    ],
    rows: [
        { name: "attaquer", values: { name: "'Attaquer'", power: "5", element: "Element.Neutre" }, raw: null },
        { name: "soin", values: { name: "'Soin'", power: "-20" }, raw: null },
    ],
});

const button = (root, label) => root.querySelectorAll("button").find(b => b.textContent === label);
const cell = (root, row, field) => root.querySelector(`input[data-row="${row}"][data-field="${field}"]`);

const tests = {
    "le tableau a une ligne par entrée et une colonne par champ"() {
        const { root } = install(freshState());
        // En-tête + deux entrées.
        assert.strictEqual(root.querySelectorAll("tr").length, 3);
        assert.strictEqual(root.querySelectorAll(".entry-name").length, 2);
        assert.ok(cell(root, 1, "power") !== null);
        assert.ok(root.textContent.includes("dataset Moves of Move"));
    },
    "un champ obligatoire vide est signalé et bloque « Appliquer »"() {
        const { root } = install(freshState());
        const apply = button(root, "Appliquer au dataset");
        assert.strictEqual(apply.disabled, false);

        const name = cell(root, 0, "name");
        name.value = "";
        name.dispatch("input");
        assert.strictEqual(apply.disabled, true);
        assert.ok(name.classList.contains("invalid"));
        assert.ok(root.textContent.includes("obligatoire"));

        name.value = "'Attaque'";
        name.dispatch("input");
        assert.strictEqual(apply.disabled, false);
    },
    "un nom d'entrée en double ou réservé bloque « Appliquer »"() {
        const { root } = install(freshState());
        const apply = button(root, "Appliquer au dataset");
        const second = root.querySelectorAll(".entry-name")[1];
        second.value = "attaquer";
        second.dispatch("input");
        assert.strictEqual(apply.disabled, true);
        assert.ok(root.textContent.includes("en double"));

        second.value = "length";
        second.dispatch("input");
        assert.ok(root.textContent.includes("réservé"));
    },
    "+ Entrée ajoute une ligne au nom libre"() {
        const { root } = install(freshState());
        button(root, "+ Entrée").dispatch("click");
        const names = root.querySelectorAll(".entry-name").map(i => i.value);
        assert.deepStrictEqual(names, ["attaquer", "soin", "entree"]);
        // Le champ obligatoire de la nouvelle ligne est vide : il faut le remplir.
        assert.strictEqual(button(root, "Appliquer au dataset").disabled, true);
    },
    "dupliquer et supprimer"() {
        const { root } = install(freshState());
        root.querySelectorAll("button").find(b => b.attributes.title === "Dupliquer").dispatch("click");
        assert.deepStrictEqual(root.querySelectorAll(".entry-name").map(i => i.value), ["attaquer", "attaquer2", "soin"]);
        root.querySelectorAll("button").filter(b => b.attributes.title === "Supprimer")[1].dispatch("click");
        assert.deepStrictEqual(root.querySelectorAll(".entry-name").map(i => i.value), ["attaquer", "soin"]);
    },
    "« Appliquer » envoie les colonnes et les entrées à l'extension"() {
        const { root, posted } = install(freshState());
        cell(root, 1, "power").value = "-15";
        cell(root, 1, "power").dispatch("input");
        button(root, "Appliquer au dataset").dispatch("click");

        const message = posted.find(m => m.type === "apply");
        assert.ok(message);
        assert.deepStrictEqual(message.fields.map(f => f.name), ["name", "power", "element"]);
        assert.strictEqual(message.rows[1].values.power, "-15");
    },
    "les valeurs possibles d'un champ sont une datalist, avec l'écriture du mode"() {
        const inline = install(freshState()).root;
        assert.deepStrictEqual(inline.querySelector("datalist").children.map(o => o.attributes.value), ["Element.Feu"]);

        const state = freshState();
        state.mode = "json";
        state.source = "moves.json";
        const json = install(state).root;
        assert.deepStrictEqual(json.querySelector("datalist").children.map(o => o.attributes.value), ["\"Feu\""]);
        assert.ok(button(json, "Appliquer au fichier JSON"));
    },
    "un dataset as ou un fichier JSON permet d'ajouter un champ ; un dataset of non"() {
        assert.strictEqual(button(install(freshState()).root, "+ Champ"), undefined);

        const state = freshState();
        state.createsMold = true;
        const { root } = install(state);
        const input = root.querySelector(".field-name");
        input.value = "rare";
        button(root, "+ Champ").dispatch("click");
        assert.ok(root.textContent.includes("rare"));
        // Entrée + quatre champs + actions.
        assert.strictEqual(root.querySelectorAll("tr")[0].children.length, 6);
    },
    "une valeur libre occupe une cellule"() {
        const state = freshState();
        state.rows.push({ name: "spe", values: {}, raw: "autre.valeur" });
        const { root } = install(state);
        assert.ok(root.querySelector("input.raw"));
        assert.strictEqual(root.querySelector("input.raw").value, "autre.valeur");
    },
    "un message (fichier introuvable) s'affiche"() {
        const state = freshState();
        state.message = "Le fichier « x.json » est introuvable";
        assert.ok(install(state).root.textContent.includes("introuvable"));
    },
};

let failed = 0;
for (const [name, test] of Object.entries(tests)) {
    try {
        test();
        console.log(`  ok   ${name}`);
    } catch (error) {
        ++failed;
        console.log(`  FAIL ${name}\n       ${String(error.stack ?? error.message).split("\n").slice(0, 4).join("\n       ")}`);
    }
}
console.log(failed === 0 ? `${Object.keys(tests).length} tests passent.` : `${failed} test(s) échouent.`);
process.exit(failed === 0 ? 0 : 1);
