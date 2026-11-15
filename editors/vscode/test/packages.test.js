"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { validateAddress, dependenciesOf } = require("../packages");
const { commandLine, title, PACKAGE_COMMANDS, COMMANDS } = require("../tasks");

// Une adresse valide, avec ou sans version.
assert.equal(validateAddress("github.com/alice/json"), null);
assert.equal(validateAddress("  gitlab.com/bob/geometry@2.0.0 "), null);
assert.equal(validateAddress("codeberg.org/carol/core@0.1.10"), null);
assert.equal(validateAddress("GitHub.com/Alice/Json"), null);

// Ce qui est refusé, avec la raison.
assert.match(validateAddress(""), /adresse du dépôt/);
assert.match(validateAddress("https://github.com/alice/json"), /sans https/);
assert.match(validateAddress("github.com/alice"), /hôte\/compte\/dépôt/);
assert.match(validateAddress("github.com/alice/sub/json"), /hôte\/compte\/dépôt/);
assert.match(validateAddress("example.com/alice/json"), /github\.com/);
assert.match(validateAddress("github.com/ali ce/json"), /lettres/);
assert.match(validateAddress("github.com/alice/json.git"), /\.git/);
assert.match(validateAddress("github.com/alice/json@^1.2"), /trois nombres/);
assert.match(validateAddress("github.com/alice/json@1.2"), /trois nombres/);
assert.match(validateAddress("github.com/alice/json@01.2.0"), /trois nombres/);

// Les dépendances d'un manifeste, triées ; un manifeste illisible n'en a pas.
assert.deepEqual(dependenciesOf('{"dependencies": {"gitlab.com/bob/geometry": "2.0.0", "github.com/alice/json": "1.2.0"}}'),
    [{ address: "github.com/alice/json", version: "1.2.0" }, { address: "gitlab.com/bob/geometry", version: "2.0.0" }]);
assert.deepEqual(dependenciesOf("{}"), []);
assert.deepEqual(dependenciesOf("pas du json"), []);
assert.deepEqual(dependenciesOf('{"dependencies": []}'), []);
assert.deepEqual(dependenciesOf('{"dependencies": {"a/b/c": 3}}'), []);

// Les commandes de paquets : leurs arguments, -d, jamais de fichier ni de --release.
assert.deepEqual(commandLine({ command: "add", args: ["github.com/alice/json@1.2.0"] }, "rgl"), { command: "rgl", args: ["add", "github.com/alice/json@1.2.0"] });
assert.deepEqual(commandLine({ command: "restore", folder: "demo", file: "a.rgl", release: true }, "rgl"), { command: "rgl", args: ["restore", "-d", "demo"] });
assert.deepEqual(commandLine({ command: "publish", args: ["--dry-run"] }, "C:/outils/rgl.dll"), { command: "dotnet", args: ["C:/outils/rgl.dll", "publish", "--dry-run"] });
for (const command of PACKAGE_COMMANDS) {
    assert.ok(command in COMMANDS, command);
    assert.ok(title({ command }).length > 0);
}

// Le manifeste de l'extension déclare chaque commande de paquets et les accepte comme tâche.
const manifest = JSON.parse(fs.readFileSync(path.join(__dirname, "..", "package.json"), "utf8"));
const declared = manifest.contributes.commands.map(c => c.command);
for (const command of ["rgl.addPackage", "rgl.removePackage", "rgl.restore", "rgl.packageTree", "rgl.outdatedPackages", "rgl.updatePackages", "rgl.publishPackage"])
    assert.ok(declared.includes(command), command);
const taskCommands = manifest.contributes.taskDefinitions[0].properties.command.enum;
for (const command of PACKAGE_COMMANDS) assert.ok(taskCommands.includes(command), command);

// Le schéma de rgl.json connaît les dépendances et la confiance.
const schema = JSON.parse(fs.readFileSync(path.join(__dirname, "..", "rgl.schema.json"), "utf8"));
assert.ok(schema.properties.dependencies);
assert.ok(schema.properties["package-trust"].properties.binaries && schema.properties["package-trust"].properties.cpp);
assert.match("github.com/alice/json", new RegExp(schema.properties.dependencies.propertyNames.pattern));
assert.doesNotMatch("example.com/alice/json", new RegExp(schema.properties.dependencies.propertyNames.pattern));

console.log("packages : ok");
