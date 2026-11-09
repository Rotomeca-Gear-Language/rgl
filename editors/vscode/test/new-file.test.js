"use strict";
const assert = require("node:assert/strict");
const { FILE_KINDS, FORGE_KINDS, validateName, quoteDescription, fileContent } = require("../new-file");

assert.deepEqual(FILE_KINDS.map(k => k.extension), [".rgl", ".forge"]);
assert.ok(FORGE_KINDS.some(k => k.kind === "gears") && FORGE_KINDS.some(k => k.kind === "mold") && FORGE_KINDS.some(k => k.kind === "extend"));

// Les noms : un identificateur, sans extension, jamais un fichier existant.
const folder = { existing: ["main.rgl", "Commandes.forge", "readme.md"] };
assert.equal(validateName("Calculs", ".forge", folder), undefined);
assert.equal(validateName("mon-fichier_2", ".forge", folder), undefined);
assert.equal(validateName("", ".forge", folder).severity, "error");
assert.match(validateName("a.forge", ".forge", folder).message, /sans l'extension/);
assert.match(validateName("2cool", ".forge", folder).message, /commence par une lettre/);
assert.match(validateName("a b", ".forge", folder).message, /commence par une lettre/);
assert.match(validateName("commandes", ".forge", folder).message, /existe déjà/);

// Un deuxième .rgl avertit sans empêcher ; un fichier de test ne compte pas.
assert.deepEqual(validateName("autre", ".rgl", folder), { message: "Ce dossier a déjà un fichier .rgl : un projet n'en a qu'un (sauf les fichiers nom.test.rgl).", severity: "warning" });
assert.equal(validateName("autre.test", ".rgl", folder)?.severity, undefined);
assert.equal(validateName("autre", ".rgl", { existing: ["Commandes.forge"] }), undefined);

// La description : entre apostrophes, entre guillemets si elle en contient une.
assert.equal(quoteDescription("Les formes"), "'Les formes'");
assert.equal(quoteDescription("  deux   lignes\nde texte "), "'deux lignes de texte'");
assert.equal(quoteDescription("L'utilisateur"), '"L\'utilisateur"');
assert.equal(quoteDescription("dit \"oui\" et l'autre"), '"dit \\"oui\\" et l\'autre"');
assert.equal(quoteDescription(""), "''");

// Le contenu.
assert.equal(fileContent(".rgl"), "train |> done;\n");
assert.equal(fileContent(".forge", { forgeKind: "gears", description: "Mes gears" }), "forge: gears\ndescription: 'Mes gears'\n\n");
assert.equal(fileContent(".forge", { forgeKind: "mold" }), "forge: mold\ndescription: ''\n\n");
assert.equal(fileContent(".forge", { forgeKind: "train", description: "Un train" }), "forge: train\ndescription: 'Un train'\n\ntrain |> done;\n");
assert.equal(fileContent(".forge"), "forge: gears\ndescription: ''\n\n");

// Les dossiers box et tools.
const { FOLDER_KINDS, validateFolderName, indexContent } = require("../new-file");
const dirs = { existing: ["Maths.box", "outils.tools", "src"] };
assert.equal(validateFolderName("Database", ".box", dirs), undefined);
assert.equal(validateFolderName("Vectors", ".tools", dirs), undefined);
assert.match(validateFolderName("maths", ".box", dirs).message, /existe déjà/);
assert.match(validateFolderName("Outils", ".tools", dirs).message, /existe déjà/);
assert.equal(validateFolderName("Maths", ".tools", dirs), undefined);
assert.match(validateFolderName("A.box", ".box", dirs).message, /sans « \.box »/);
assert.match(validateFolderName("2x", ".box", dirs).message, /commence par une lettre/);
assert.equal(validateFolderName("  ", ".tools", dirs).severity, "error");
assert.equal(FOLDER_KINDS.box.suffix, ".box");
assert.equal(FOLDER_KINDS.tools.suffix, ".tools");

// L'index d'une box : le genre, la description et l'aide à écrire les imports et l'export.
assert.equal(indexContent("La base"), "forge: index\ndescription: 'La base'\n\n// Importez les fichiers de la box, puis exportez leurs noms :\n// import Fichier;\n// export {...Fichier};\n");
assert.ok(indexContent().startsWith("forge: index\ndescription: ''\n"));

console.log("new-file : ok");
