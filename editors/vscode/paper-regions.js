"use strict";
// Les zones de RGL d'une page `.paper` : les blocs `@rgl { … }` et les interpolations `@@ … @@`. Même découpage que le compilateur : les blocs d'abord
// (une accolade dans un texte entre guillemets, apostrophes ou accents graves ne ferme rien), puis les interpolations dans le reste de la page.
// Ce fichier ne dépend pas de VS Code.

/** Le caractère peut faire partie d'un nom. */
function isName(c) {
    return /[\p{L}\p{N}_]/u.test(c);
}

/** L'accolade qui ferme celle de `open` ; -1 si le bloc n'est pas fermé. */
function closingBrace(text, open) {
    let depth = 0;
    let quote = "";
    let escaped = false;
    for (let i = open; i < text.length; i++) {
        const c = text[i];
        if (quote) {
            if (escaped) escaped = false;
            else if (c === "\\") escaped = true;
            else if (c === quote) quote = "";
            continue;
        }
        if (c === "'" || c === '"' || c === "`") {
            quote = c;
            continue;
        }
        if (c === "{") depth++;
        else if (c === "}" && --depth === 0) return i;
    }
    return -1;
}

/**
 * @param {string} text Le contenu de la page.
 * @returns {{ start: number, end: number, outerStart: number, outerEnd: number, isBlock: boolean }[]}
 *   `start`/`end` : le RGL lui-même ; `outerStart`/`outerEnd` : avec `@rgl {` et `}`, ou `@@` et `@@`.
 */
function findRegions(text) {
    const regions = [];
    const markup = [];
    let markupStart = 0;
    for (let i = 0; i < text.length;) {
        if (!text.startsWith("@rgl", i) || (i > 0 && isName(text[i - 1]))) {
            i++;
            continue;
        }
        let cursor = i + 4;
        while (cursor < text.length && /\s/.test(text[cursor])) cursor++;
        if (text[cursor] !== "{") {
            i += 4;
            continue;
        }
        const close = closingBrace(text, cursor);
        const end = close < 0 ? text.length : close;
        markup.push([markupStart, i]);
        regions.push({ start: cursor + 1, end, outerStart: i, outerEnd: close < 0 ? text.length : close + 1, isBlock: true });
        if (close < 0) {
            markupStart = text.length;
            break;
        }
        i = close + 1;
        markupStart = i;
    }
    if (markupStart < text.length) markup.push([markupStart, text.length]);

    for (const [start, end] of markup) {
        for (let position = start; position < end;) {
            const open = text.indexOf("@@", position);
            if (open < 0 || open >= end) break;
            const close = text.indexOf("@@", open + 2);
            if (close < 0 || close >= end) {
                const lineEnd = text.indexOf("\n", open + 2);
                const stop = lineEnd < 0 || lineEnd > end ? end : lineEnd;
                regions.push({ start: open + 2, end: stop, outerStart: open, outerEnd: stop, isBlock: false });
                break;
            }
            regions.push({ start: open + 2, end: close, outerStart: open, outerEnd: close + 2, isBlock: false });
            position = close + 2;
        }
    }
    return regions.sort((a, b) => a.start - b.start);
}

/**
 * Découpe une zone en morceaux de lignes : les lignes du milieu en entier, la première et la dernière en partie. Sert à poser un fond sans que la
 * zone ait l'air déchirée : les lignes pleines s'étendent jusqu'au bord de l'éditeur.
 * @param {string} text
 * @param {number} from
 * @param {number} to
 * @returns {{ whole: [number, number][], partial: [number, number][] }} Des intervalles de positions du texte ; `whole` : lignes entières (début de ligne).
 */
function lineParts(text, from, to) {
    const firstEnd = text.indexOf("\n", from);
    if (firstEnd < 0 || firstEnd >= to) return { whole: [], partial: [[from, to]] };

    const whole = [];
    let lineStart = firstEnd + 1;
    for (;;) {
        const lineEnd = text.indexOf("\n", lineStart);
        if (lineEnd < 0 || lineEnd >= to) break;
        whole.push([lineStart, lineEnd]);
        lineStart = lineEnd + 1;
    }
    return { whole, partial: [[from, firstEnd], [lineStart, to]] };
}

/**
 * Les noms des états `reactive` d'une page (`reactive int count: 0;`), lus dans ses blocs `@rgl`. Dans le C++ engendré, un état est le champ `state.count` du
 * paramètre `state` des gears de la page : le débogueur doit l'évaluer ainsi.
 * @param {string} text Le contenu de la page.
 * @returns {string[]}
 */
function reactiveNames(text) {
    const names = [];
    for (const region of findRegions(text).filter(r => r.isBlock)) {
        const block = text.slice(region.start, region.end);
        const declaration = /\breactive\s+(?:mut\s+)?[\s\S]+?\s+([A-Za-z_][A-Za-z0-9_]*)\s*:/g;
        for (let match = declaration.exec(block); match !== null; match = declaration.exec(block)) names.push(match[1]);
    }
    return names;
}

module.exports = { findRegions, lineParts, reactiveNames };
