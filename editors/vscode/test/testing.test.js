"use strict";
const assert = require("node:assert/strict");
const { isTestFile, testName, runCommand, readFailure } = require("../testing");

assert.equal(isTestFile("C:\\p\\addition.test.rgl"), true);
assert.equal(isTestFile("main.rgl"), false);
assert.equal(isTestFile("x.test.forge"), false);
assert.equal(testName("C:\\p\\addition.test.rgl"), "addition");

assert.deepEqual(runCommand("a.test.rgl", "rgl"), { command: "rgl", args: ["exec", "a.test.rgl"] });
assert.deepEqual(runCommand("a.test.rgl", "C:/x/rgl.dll"), { command: "dotnet", args: ["C:/x/rgl.dll", "exec", "a.test.rgl"] });

// Le rapport des vérifications de la std : le dernier ÉCHEC, avec sa place (un chemin Windows contient des deux-points).
assert.deepEqual(readFailure("1 ok\nÉCHEC (C:\\p\\add.test.rgl:12:5) : attendu 4, reçu 5 - somme\n"),
    { message: "attendu 4, reçu 5 - somme", file: "C:\\p\\add.test.rgl", line: 12, column: 5 });
assert.deepEqual(readFailure("ÉCHEC (a.rgl:1:1) : un\nÉCHEC (a.rgl:2:3) : deux"), { message: "deux", file: "a.rgl", line: 2, column: 3 });

// Une erreur de compilation : le message du diagnostic et sa place, sans les couleurs.
assert.deepEqual(readFailure("\x1b[31merror[E003]\x1b[0m: type incorrect\n --> add.test.rgl:3:9\n  |\n"),
    { message: "[E003] type incorrect", file: "add.test.rgl", line: 3, column: 9 });
assert.deepEqual(readFailure("error[E035]: gear inconnu\n"), { message: "[E035] gear inconnu" });

// Sans rapport : la fin de la sortie, ou une phrase.
assert.deepEqual(readFailure("a\nb\n\nc\n"), { message: "a\nb\nc" });
assert.deepEqual(readFailure(""), { message: "le test a échoué sans rien écrire" });

console.log("testing : ok");
