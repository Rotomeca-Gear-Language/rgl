// Le cœur de l'éditeur de dataset, sans rien de VS Code : lire des entrées d'un fichier JSON, écrire les entrées d'un dataset en RGL ou
// en JSON, et signaler ce qui ne va pas. Les cellules du tableau sont du texte : du RGL écrit (`'Attaquer'`, `5`, `Element.Feu`,
// `Classes.mage`) pour un dataset écrit dans le source, du JSON (`"Attaquer"`, `5`, `"Feu"`) pour un fichier de données.

"use strict";

/** Les noms qu'une entrée ne peut pas prendre : ceux des membres de tout dataset. */
const RESERVED = ["id", "length", "get", "find", "name"];

const IDENTIFIER = /^[A-Za-z_][A-Za-z0-9_]*$/;

/**
 * @typedef {{ label: string, inline: string, json: string }} Choice
 * @typedef {{ name: string, type: string | null, required: boolean, choices: Choice[] }} Field
 * @typedef {{ name: string, values: Record<string, string>, raw?: string | null }} Row
 */

/** Une entrée vide : toutes les cellules sont vides, sauf celles que le champ propose avec une valeur par défaut de l'éditeur. */
function emptyRow(name, fields) {
    /** @type {Record<string, string>} */
    const values = {};
    for (const field of fields) values[field.name] = "";
    return { name, values, raw: null };
}

/** Un nom d'entrée libre : `entree`, `entree2`, `entree3`… */
function freshName(rows, base = "entree") {
    const taken = new Set(rows.map(r => r.name));
    if (!taken.has(base)) return base;
    for (let i = 2; ; ++i) if (!taken.has(base + i)) return base + i;
}

/**
 * Les entrées en RGL : `{ … }` avec une entrée par ligne, `nom: {champ: valeur, …},`. Les champs vides sont omis (le mold donne leur défaut) et
 * l'ordre des champs est celui des colonnes.
 * @param {Field[]} fields
 * @param {Row[]} rows
 */
function inlineBody(fields, rows, indent = "    ") {
    const lines = rows.map(row => {
        if (row.raw != null && row.raw !== "") return `${indent}${row.name}: ${row.raw},`;
        const written = fields.filter(f => (row.values[f.name] ?? "").trim() !== "").map(f => `${f.name}: ${row.values[f.name].trim()}`);
        return `${indent}${row.name}: {${written.join(", ")}},`;
    });
    return lines.length === 0 ? "{\n}" : `{\n${lines.join("\n")}\n}`;
}

/** Le texte d'une cellule JSON comme valeur : du JSON s'il en est, sinon une chaîne (`Feu` s'écrit `"Feu"`). */
function parseCell(text) {
    const trimmed = text.trim();
    try {
        return JSON.parse(trimmed);
    } catch {
        return trimmed;
    }
}

/**
 * Les entrées en JSON : un objet dont les clés sont les noms, ou un tableau d'objets avec un champ `id` (quand le fichier d'origine en était un).
 * @param {Field[]} fields
 * @param {Row[]} rows
 * @param {boolean} asArray
 */
function jsonText(fields, rows, asArray) {
    const record = row => {
        /** @type {Record<string, unknown>} */
        const value = {};
        for (const field of fields) {
            const cell = (row.values[field.name] ?? "").trim();
            if (cell !== "") value[field.name] = parseCell(cell);
        }
        return value;
    };
    const data = asArray
        ? rows.map(row => ({ id: row.name, ...record(row) }))
        : Object.fromEntries(rows.map(row => [row.name, record(row)]));
    return JSON.stringify(data, null, 2) + "\n";
}

/**
 * Les entrées d'un fichier JSON. Les clés que le mold ne connaît pas deviennent de nouvelles colonnes (sans type).
 * @param {string} text
 * @param {Field[]} fields
 * @returns {{ rows: Row[], fields: Field[], asArray: boolean } | { error: string }}
 */
function readJson(text, fields) {
    let data;
    try {
        data = text.trim() === "" ? {} : JSON.parse(text);
    } catch (error) {
        return { error: `JSON invalide : ${error.message}` };
    }

    const asArray = Array.isArray(data);
    const entries = asArray ? data.map(item => [item?.id, item]) : Object.entries(data ?? {});
    const columns = fields.map(f => ({ ...f }));
    /** @type {Row[]} */
    const rows = [];
    for (const [name, record] of entries) {
        if (typeof name !== "string" || record === null || typeof record !== "object" || Array.isArray(record))
            return { error: asArray ? "chaque entrée d'un tableau est un objet dont le champ « id » est une chaîne" : `l'entrée « ${name} » doit être un objet` };

        /** @type {Record<string, string>} */
        const values = {};
        for (const [key, value] of Object.entries(record)) {
            if (asArray && key === "id") continue;
            if (!columns.some(c => c.name === key)) columns.push({ name: key, type: null, required: false, choices: [] });
            values[key] = JSON.stringify(value);
        }
        rows.push({ name, values, raw: null });
    }
    return { rows, fields: columns, asArray };
}

/**
 * Ce qui ne va pas dans le tableau : un nom d'entrée invalide, réservé ou en double, un champ obligatoire vide. Chaque problème dit sa ligne et sa colonne.
 * @param {Field[]} fields
 * @param {Row[]} rows
 * @returns {{ row: number, field: string | null, message: string }[]}
 */
function validate(fields, rows) {
    const issues = [];
    const seen = new Set();
    rows.forEach((row, index) => {
        if (!IDENTIFIER.test(row.name)) issues.push({ row: index, field: null, message: `« ${row.name} » n'est pas un nom : lettres, chiffres et _, sans commencer par un chiffre` });
        else if (RESERVED.includes(row.name)) issues.push({ row: index, field: null, message: `« ${row.name} » est réservé (${RESERVED.join(", ")})` });
        else if (seen.has(row.name)) issues.push({ row: index, field: null, message: `l'entrée « ${row.name} » est en double` });
        seen.add(row.name);

        if (row.raw != null && row.raw !== "") return;
        for (const field of fields) {
            if (field.required && (row.values[field.name] ?? "").trim() === "") issues.push({ row: index, field: field.name, message: `« ${field.name} » n'a pas de valeur par défaut : il est obligatoire` });
        }
    });
    return issues;
}

/** Le nom d'un fichier JSON écrit après `from` : `moves.json` ou `moves` désignent `moves`. */
function jsonBaseName(source) {
    return source.replace(/^.*[\\/]/, "").replace(/\.json$/i, "");
}

const api = { RESERVED, emptyRow, freshName, inlineBody, jsonText, readJson, validate, parseCell, jsonBaseName };

// Chargé par Node (extension, tests) ou par la page du tableau (balise script).
if (typeof module !== "undefined") module.exports = api;
else globalThis.RglDatasetModel = api;
