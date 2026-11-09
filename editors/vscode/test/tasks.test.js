"use strict";
const assert = require("node:assert/strict");
const { commandLine, title, defaultDefinitions, COMMANDS } = require("../tasks");

// Un compilateur du PATH.
assert.deepEqual(commandLine({ command: "compile" }, "rgl"), { command: "rgl", args: ["compile"] });

// Un .dll se lance avec dotnet.
assert.deepEqual(commandLine({ command: "exec", release: true, file: "main.rgl" }, "C:/outils/rgl.dll"),
    { command: "dotnet", args: ["C:/outils/rgl.dll", "exec", "main.rgl", "--release"] });

// Un dossier de projet : -d. Un fichier l'emporte.
assert.deepEqual(commandLine({ command: "compile", folder: "demo" }, "rgl"), { command: "rgl", args: ["compile", "-d", "demo"] });
assert.deepEqual(commandLine({ command: "compile", folder: "demo", file: "a.rgl" }, "rgl"), { command: "rgl", args: ["compile", "a.rgl"] });

// check n'a pas de profil.
assert.deepEqual(commandLine({ command: "check", release: true }, "rgl"), { command: "rgl", args: ["check"] });

assert.throws(() => commandLine({ command: "format" }, "rgl"), /commande RGL inconnue/);

assert.equal(title({ command: "exec" }), "Exécuter");
assert.equal(title({ command: "compile", release: true }), "Compiler (release)");
assert.equal(title({ command: "check", release: true }), "Vérifier");

for (const definition of defaultDefinitions()) assert.ok(definition.command in COMMANDS);
assert.equal(defaultDefinitions().length, 5);
console.log("tasks : ok");
