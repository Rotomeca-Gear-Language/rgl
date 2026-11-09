// Tests du cœur de l'éditeur de dataset : `node test/dataset-model.test.js` depuis editors/vscode (aucune dépendance).

"use strict";

const assert = require("assert");
const { emptyRow, freshName, inlineBody, jsonText, readJson, validate, parseCell, jsonBaseName } = require("../dataset-model");

const fields = [
    { name: "name", type: "string", required: true, choices: [] },
    { name: "power", type: "int", required: false, choices: [] },
    { name: "element", type: "Element", required: false, choices: [{ label: "Feu", inline: "Element.Feu", json: "\"Feu\"" }] },
];
const rows = [
    { name: "attaquer", values: { name: "'Attaquer'", power: "5", element: "Element.Neutre" }, raw: null },
    { name: "soin", values: { name: "'Soin'", power: "", element: "" }, raw: null },
];

// Les mêmes entrées, écrites comme les cellules d'un fichier JSON.
const jsonRows = [
    { name: "attaquer", values: { name: "\"Attaquer\"", power: "5", element: "\"Neutre\"" }, raw: null },
    { name: "soin", values: { name: "\"Soin\"", power: "", element: "" }, raw: null },
];

const tests = {
    "le corps RGL écrit une entrée par ligne et omet les cellules vides"() {
        assert.strictEqual(inlineBody(fields, rows),
            "{\n    attaquer: {name: 'Attaquer', power: 5, element: Element.Neutre},\n    soin: {name: 'Soin'},\n}");
    },
    "un dataset sans entrée s'écrit avec des accolades vides"() {
        assert.strictEqual(inlineBody(fields, []), "{\n}");
    },
    "une valeur libre s'écrit telle quelle"() {
        assert.strictEqual(inlineBody(fields, [{ name: "x", values: {}, raw: "autre.valeur" }]), "{\n    x: autre.valeur,\n}");
    },
    "le JSON objet a pour clés les noms des entrées"() {
        const text = jsonText(fields, jsonRows, false);
        assert.deepStrictEqual(JSON.parse(text), { attaquer: { name: "Attaquer", power: 5, element: "Neutre" }, soin: { name: "Soin" } });
        assert.ok(text.endsWith("\n"));
    },
    "le JSON tableau met le nom dans le champ id"() {
        assert.deepStrictEqual(JSON.parse(jsonText(fields, jsonRows, true))[1], { id: "soin", name: "Soin" });
    },
    "une cellule JSON qui n'en est pas devient une chaîne"() {
        assert.strictEqual(parseCell("Feu"), "Feu");
        assert.strictEqual(parseCell("\"Feu\""), "Feu");
        assert.strictEqual(parseCell("12"), 12);
        assert.strictEqual(parseCell("true"), true);
    },
    "lire un JSON objet donne des lignes et de nouvelles colonnes"() {
        const result = readJson("{\"a\": {\"name\": \"A\", \"rare\": true}, \"b\": {\"power\": 2}}", fields);
        assert.deepStrictEqual(result.rows.map(r => r.name), ["a", "b"]);
        assert.strictEqual(result.rows[0].values.name, "\"A\"");
        assert.strictEqual(result.rows[0].values.rare, "true");
        assert.deepStrictEqual(result.fields.map(f => f.name), ["name", "power", "element", "rare"]);
        assert.strictEqual(result.asArray, false);
    },
    "lire un JSON tableau prend le nom dans id et ne le garde pas comme colonne"() {
        const result = readJson("[{\"id\": \"a\", \"name\": \"A\"}]", fields);
        assert.deepStrictEqual(result.rows.map(r => r.name), ["a"]);
        assert.ok(!result.fields.some(f => f.name === "id"));
        assert.strictEqual(result.asArray, true);
    },
    "un JSON invalide ou mal formé dit pourquoi"() {
        assert.ok(readJson("{", fields).error.startsWith("JSON invalide"));
        assert.ok(readJson("[1]", fields).error.includes("« id »"));
        assert.ok(readJson("{\"a\": 3}", fields).error.includes("« a »"));
    },
    "écrire puis relire un JSON rend les mêmes entrées"() {
        const again = readJson(jsonText(fields, jsonRows, false), fields);
        assert.deepStrictEqual(again.rows.map(r => r.name), ["attaquer", "soin"]);
        assert.strictEqual(again.rows[0].values.power, "5");
    },
    "la validation signale nom invalide, réservé, en double et champ obligatoire vide"() {
        const issues = validate(fields, [
            { name: "1a", values: { name: "'x'" } },
            { name: "id", values: { name: "'x'" } },
            { name: "ok", values: { name: "" } },
            { name: "ok", values: { name: "'y'" } },
        ]);
        assert.deepStrictEqual(issues.map(i => [i.row, i.field]), [[0, null], [1, null], [2, "name"], [3, null]]);
    },
    "des entrées valides ne donnent aucun problème"() {
        assert.deepStrictEqual(validate(fields, rows), []);
    },
    "freshName évite les noms pris"() {
        assert.strictEqual(freshName([]), "entree");
        assert.strictEqual(freshName([{ name: "entree" }, { name: "entree2" }]), "entree3");
    },
    "emptyRow a une cellule vide par colonne"() {
        assert.deepStrictEqual(emptyRow("x", fields).values, { name: "", power: "", element: "" });
    },
    "jsonBaseName retire le dossier et l'extension"() {
        assert.strictEqual(jsonBaseName("data/moves.json"), "moves");
        assert.strictEqual(jsonBaseName("moves"), "moves");
    },
};

let failed = 0;
for (const [name, test] of Object.entries(tests)) {
    try {
        test();
        console.log(`  ok   ${name}`);
    } catch (error) {
        ++failed;
        console.log(`  FAIL ${name}\n       ${error.message.split("\n").join("\n       ")}`);
    }
}
console.log(failed === 0 ? `${Object.keys(tests).length} tests passent.` : `${failed} test(s) échouent.`);
process.exit(failed === 0 ? 0 : 1);
