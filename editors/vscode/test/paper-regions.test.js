"use strict";
const assert = require("node:assert/strict");
const { findRegions, lineParts, reactiveNames } = require("../paper-regions");

const page = "<h1>@@ titre @@</h1>\n@rgl {\n  const string a: '}';\n  gear f(): int => 1;\n}\n<p>@@ x @@ et @@ y @@</p>";
const regions = findRegions(page);

// Le RGL lui-même, dans l'ordre de la page.
assert.deepEqual(regions.map(r => page.slice(r.start, r.end)), [" titre ", "\n  const string a: '}';\n  gear f(): int => 1;\n", " x ", " y "]);
assert.deepEqual(regions.map(r => r.isBlock), [false, true, false, false]);

// Le contour : @rgl { … } et @@ … @@.
assert.equal(page.slice(regions[0].outerStart, regions[0].outerEnd), "@@ titre @@");
assert.ok(page.slice(regions[1].outerStart, regions[1].outerEnd).startsWith("@rgl {"));
assert.ok(page.slice(regions[1].outerStart, regions[1].outerEnd).endsWith("}"));

// Un nom collé à @rgl n'ouvre rien ; sans accolade non plus.
assert.deepEqual(findRegions("a@rgl { 1 } @rgl x"), []);

// Un bloc non fermé court jusqu'à la fin ; une interpolation non fermée jusqu'à la fin de la ligne.
assert.deepEqual(findRegions("@rgl { const int a: 1;").map(r => [r.start, r.end]), [[6, 22]]);
const open = findRegions("<p>@@ abc\n<b>x</b>");
assert.equal("<p>@@ abc\n<b>x</b>".slice(open[0].start, open[0].end), " abc");

// Les lignes d'une zone : celles du milieu en entier.
const block = regions[1];
const parts = lineParts(page, block.outerStart, block.outerEnd);
assert.equal(parts.whole.length, 2);
assert.equal(parts.partial.length, 2);
assert.deepEqual(lineParts("a @@ b @@ c", 2, 9), { whole: [], partial: [[2, 9]] });

// Les états réactifs : dans les blocs seulement, sur une ou plusieurs lignes.
const reactivePage = ["<p>reactive int hors: 1;</p>", "@rgl {", "  reactive int count: 0;", "  reactive mut [string] noms:", "    [];", "  const int autre: 2;", "}", ""].join("\n");
assert.deepEqual(reactiveNames(reactivePage), ["count", "noms"]);
assert.deepEqual(reactiveNames("<h1>@@ x @@</h1>"), []);

console.log("paper-regions : ok");
