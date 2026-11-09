// La page de l'éditeur de dataset : un tableau dont chaque ligne est une entrée et chaque colonne un champ du mold. Elle ne touche pas aux
// fichiers : elle envoie « appliquer » à l'extension, qui réécrit le dataset (ou son fichier JSON) dans l'éditeur, sans l'enregistrer.

/* global acquireVsCodeApi, RglDatasetModel */
(function () {
    "use strict";

    const vscode = acquireVsCodeApi();
    const model = RglDatasetModel;
    const root = document.getElementById("root");

    /** @type {{ mode: "inline" | "json", name: string, mold: string, createsMold: boolean, source: string | null, file: string | null, fields: any[], rows: any[], asArray: boolean, message: string | null } | null} */
    let state = null;
    let status = null;
    let applyButton = null;

    window.addEventListener("message", event => {
        if (event.data.type !== "init") return;
        state = event.data.state;
        render();
    });
    vscode.postMessage({ type: "ready" });

    function el(tag, attributes, ...children) {
        const element = document.createElement(tag);
        for (const [key, value] of Object.entries(attributes ?? {})) {
            if (key === "class") element.className = value;
            else if (key.startsWith("on")) element.addEventListener(key.slice(2), value);
            else if (value !== false && value != null) element.setAttribute(key, value === true ? "" : value);
        }
        for (const child of children.flat()) element.append(child);
        return element;
    }

    function choiceValue(choice) {
        return state.mode === "json" ? choice.json : choice.inline;
    }

    function render() {
        root.replaceChildren();
        if (!state) return;

        const canAddColumns = state.createsMold || state.mode === "json";
        const title = state.mode === "json"
            ? `dataset ${state.name} from "${state.source}" · ${state.file ?? "fichier introuvable"}`
            : `dataset ${state.name} ${state.createsMold ? "as" : "of"} ${state.mold}`;
        const newField = el("input", { type: "text", placeholder: "nouveau champ", class: "field-name", id: "new-field" });
        applyButton = el("button", { class: "primary", onclick: apply }, state.mode === "json" ? "Appliquer au fichier JSON" : "Appliquer au dataset");
        status = el("div", { class: "status" });

        root.append(
            el("div", { class: "toolbar" },
                el("h1", {}, title),
                el("div", { class: "buttons" },
                    el("button", { onclick: addRow }, "+ Entrée"),
                    canAddColumns ? [newField, el("button", { onclick: () => addColumn(newField) }, "+ Champ")] : [],
                    el("button", { onclick: () => vscode.postMessage({ type: "reload" }) }, "Recharger"),
                    applyButton)),
            state.message ? el("div", { class: "notice" }, state.message) : [],
            status,
            el("div", { class: "table-wrap" }, table()),
            datalists(),
            el("p", { class: "hint" }, state.mode === "json"
                ? "Chaque cellule est du JSON : \"texte\", 5, true, [1, 2]. Une cellule vide n'écrit rien. « Appliquer » modifie le fichier dans l'éditeur ; enregistrez-le ensuite."
                : "Chaque cellule est du RGL : 'texte', 5, Element.Feu, Classes.mage. Une cellule vide n'écrit rien (le mold donne la valeur par défaut). « Appliquer » réécrit les entrées du dataset dans l'éditeur ; enregistrez ensuite."));
        validate();
    }

    function table() {
        const head = el("tr", {}, el("th", {}, "Entrée"), state.fields.map(field =>
            el("th", { title: field.type ?? "type déduit des valeurs" },
                field.name + (field.required ? " *" : ""),
                el("div", { class: "type" }, field.type ?? ""))), el("th", {}, ""));
        const body = state.rows.map((row, index) => rowElement(row, index));
        return el("table", {}, el("thead", {}, head), el("tbody", {}, body));
    }

    function datalists() {
        return el("div", { class: "lists" }, state.fields.filter(f => f.choices.length > 0).map(field =>
            el("datalist", { id: "list-" + field.name }, field.choices.map(c => el("option", { value: choiceValue(c) }, c.label)))));
    }

    function rowElement(row, index) {
        const name = el("input", { type: "text", value: row.name, class: "entry-name", "data-row": index, oninput: event => { row.name = event.target.value; validate(); } });
        const cells = row.raw != null && row.raw !== ""
            ? [el("td", { colspan: state.fields.length }, el("input", { type: "text", value: row.raw, class: "raw", oninput: event => { row.raw = event.target.value; validate(); } }))]
            : state.fields.map(field => el("td", {}, el("input", {
                type: "text", value: row.values[field.name] ?? "", "data-row": index, "data-field": field.name,
                list: field.choices.length > 0 ? "list-" + field.name : null, placeholder: field.required ? "obligatoire" : "",
                oninput: event => { row.values[field.name] = event.target.value; validate(); },
            })));
        return el("tr", {}, el("td", {}, name), cells, el("td", { class: "actions" },
            el("button", { title: "Dupliquer", onclick: () => duplicateRow(index) }, "⧉"),
            el("button", { title: "Supprimer", onclick: () => deleteRow(index) }, "✕")));
    }

    function addRow() {
        const row = model.emptyRow(model.freshName(state.rows), state.fields);
        state.rows.push(row);
        render();
        const names = root.querySelectorAll(".entry-name");
        names[names.length - 1]?.focus();
        names[names.length - 1]?.select();
    }

    function addColumn(input) {
        const name = input.value.trim();
        if (!/^[A-Za-z_][A-Za-z0-9_]*$/.test(name) || state.fields.some(f => f.name === name)) {
            input.classList.add("invalid");
            return;
        }
        state.fields.push({ name, type: null, required: false, choices: [] });
        render();
    }

    function duplicateRow(index) {
        const copy = JSON.parse(JSON.stringify(state.rows[index]));
        copy.name = model.freshName(state.rows, copy.name);
        state.rows.splice(index + 1, 0, copy);
        render();
    }

    function deleteRow(index) {
        state.rows.splice(index, 1);
        render();
    }

    /** Montre les problèmes (cellules en rouge, liste en haut) et ne laisse appliquer que des entrées valides. */
    function validate() {
        const issues = model.validate(state.fields, state.rows);
        for (const input of root.querySelectorAll("input.invalid")) input.classList.remove("invalid");
        for (const issue of issues) {
            const selector = issue.field === null ? `.entry-name[data-row="${issue.row}"]` : `input[data-row="${issue.row}"][data-field="${issue.field}"]`;
            root.querySelector(selector)?.classList.add("invalid");
        }
        status.replaceChildren(...issues.slice(0, 6).map(issue => el("div", { class: "issue" }, `Ligne ${issue.row + 1} · ${issue.message}`)));
        if (issues.length > 6) status.append(el("div", { class: "issue" }, `… et ${issues.length - 6} autre(s)`));
        if (issues.length === 0) status.append(el("div", { class: "ok" }, `${state.rows.length} entrée(s), ${state.fields.length} champ(s)`));
        applyButton.disabled = issues.length > 0;
        return issues.length === 0;
    }

    function apply() {
        if (!validate()) return;
        vscode.postMessage({ type: "apply", fields: state.fields, rows: state.rows });
    }
})();
