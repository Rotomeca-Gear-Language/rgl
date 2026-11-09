"use strict";
const assert = require("node:assert/strict");
const path = require("node:path");
const { findProject, entryFile, projectEntryFor, projectName, executablePath, chooseDebugger, launchConfiguration, STEP_AVOID_PATTERN } = require("../debug");

/** Un faux système de fichiers : des chemins de fichiers (avec leur contenu) et de dossiers. */
function fakeFiles(files, directories = []) {
    const norm = p => path.normalize(p);
    const all = new Map(Object.entries(files).map(([p, c]) => [norm(p), c]));
    const dirs = new Set(directories.map(norm));
    for (const p of all.keys()) dirs.add(path.dirname(p));
    return {
        existsSync: p => all.has(norm(p)) || dirs.has(norm(p)),
        readFileSync: p => all.get(norm(p)),
        statSync: p => ({ isDirectory: () => dirs.has(norm(p)) }),
        readdirSync: p => [...all.keys()].filter(f => path.dirname(f) === norm(p)).map(f => path.basename(f)),
    };
}

const root = path.resolve("/demo");
const files = fakeFiles({
    [path.join(root, "rgl.json")]: '{ "name": "calc", "version": "1" }',
    [path.join(root, "main.rgl")]: "",
    [path.join(root, "add.test.rgl")]: "",
    [path.join(root, "src", "ops.forge")]: "",
});

// Le projet se trouve en remontant depuis un fichier ou un dossier.
assert.deepEqual(findProject(path.join(root, "src", "ops.forge"), files), { root, name: "calc" });
assert.deepEqual(findProject(root, files), { root, name: "calc" });
assert.equal(findProject(path.resolve("/ailleurs/x.rgl"), fakeFiles({})), undefined);

// Un manifeste illisible ne bloque pas : le nom retombe sur le fichier.
const broken = fakeFiles({ [path.join(root, "rgl.json")]: "{ pas du json" });
assert.deepEqual(findProject(root, broken), { root, name: undefined });

// Le fichier d'entrée : le fichier actif, sinon l'unique .rgl hors tests.
const project = { root, name: "calc" };
assert.equal(entryFile(project, path.join(root, "add.test.rgl"), files), path.join(root, "add.test.rgl"));
assert.equal(entryFile(project, path.join(root, "src", "ops.forge"), files), path.join(root, "main.rgl"));

// Depuis un fichier quelconque du projet, le .rgl à lancer est celui du projet : un .forge, un .paper, le .rgl lui-même (même un test).
assert.deepEqual(projectEntryFor(path.join(root, "src", "ops.forge"), files), { entry: path.join(root, "main.rgl"), project: { root, name: "calc" } });
assert.equal(projectEntryFor(path.join(root, "Home.paper"), files).entry, path.join(root, "main.rgl"));
assert.equal(projectEntryFor(path.join(root, "add.test.rgl"), files).entry, path.join(root, "add.test.rgl"));
assert.equal(projectEntryFor(path.join(root, "main.rgl"), files).entry, path.join(root, "main.rgl"));
// Hors projet, ou plusieurs .rgl : pas de fichier, et pourquoi.
assert.match(projectEntryFor(path.resolve("/ailleurs/x.forge"), fakeFiles({})).reason, /aucun rgl\.json/);
const two = fakeFiles({ [path.join(root, "rgl.json")]: "{}", [path.join(root, "a.rgl")]: "", [path.join(root, "b.rgl")]: "" });
const ambiguous = projectEntryFor(path.join(root, "x.forge"), two);
assert.equal(ambiguous.entry, undefined);
assert.match(ambiguous.reason, /pas un seul fichier \.rgl/);

// Le nom et l'exécutable, comme le compilateur les écrit.
assert.equal(projectName(project, path.join(root, "main.rgl")), "calc");
assert.equal(projectName(project, path.join(root, "add.test.rgl")), "calc-add");
assert.equal(projectName({ root }, path.join(root, "seul.rgl")), "seul");
assert.equal(executablePath(project, path.join(root, "main.rgl"), "debug", "win32"), path.join(root, "out", "debug", "calc.exe"));
assert.equal(executablePath(project, path.join(root, "add.test.rgl"), "debug", "linux"), path.join(root, "out", "debug", "calc-add"));

// Le débogueur. Sous Windows Zig écrit un PDB que gdb ne lit pas : jamais gdb, lldb (CodeLLDB) d'abord, sinon le débogueur de Visual Studio.
const both = new Set(["ms-vscode.cpptools", "vadimcn.vscode-lldb"]);
assert.deepEqual(chooseDebugger("auto", both, "win32"), { kind: "lldb" });
assert.deepEqual(chooseDebugger(undefined, new Set(["ms-vscode.cpptools"]), "win32"), { kind: "vsdbg" });
assert.deepEqual(chooseDebugger("vsdbg", both, "win32"), { kind: "vsdbg" });
assert.match(chooseDebugger("gdb", both, "win32").error, /PDB/);
assert.match(chooseDebugger("vsdbg", new Set(["vadimcn.vscode-lldb"]), "win32").error, /ms-vscode\.cpptools/);
assert.match(chooseDebugger("auto", new Set(), "win32").error, /aucun débogueur/);

// Ailleurs (ELF, DWARF) : gdb d'abord.
assert.deepEqual(chooseDebugger("auto", both, "linux"), { kind: "gdb" });
assert.deepEqual(chooseDebugger(undefined, new Set(["vadimcn.vscode-lldb"]), "linux"), { kind: "lldb" });
assert.deepEqual(chooseDebugger("lldb", both, "linux"), { kind: "lldb" });
assert.match(chooseDebugger("gdb", new Set(["vadimcn.vscode-lldb"]), "linux").error, /ms-vscode\.cpptools/);
assert.match(chooseDebugger("vsdbg", both, "linux").error, /Windows/);
assert.match(chooseDebugger("auto", new Set(), "linux").error, /aucun débogueur/);

// Les configurations.
const gdb = launchConfiguration("gdb", "/x/calc.exe", "/x", { args: ["a"], env: { K: "v" }, stopAtEntry: true, debuggerPath: "C:/gdb.exe" });
assert.equal(gdb.type, "cppdbg");
assert.equal(gdb.MIMode, "gdb");
assert.equal(gdb.miDebuggerPath, "C:/gdb.exe");
assert.deepEqual(gdb.environment, [{ name: "K", value: "v" }]);
assert.equal(gdb.stopAtEntry, true);
assert.equal("miDebuggerPath" in launchConfiguration("gdb", "p", "/x"), false);

const vsdbg = launchConfiguration("vsdbg", "C:/x/calc.exe", "C:/x", { args: ["a"], env: { K: "v" }, stopAtEntry: true });
assert.equal(vsdbg.type, "cppvsdbg");
assert.equal(vsdbg.console, "integratedTerminal");
assert.deepEqual(vsdbg.environment, [{ name: "K", value: "v" }]);
assert.equal(vsdbg.stopAtEntry, true);
assert.equal("MIMode" in vsdbg, false);

const lldb = launchConfiguration("lldb", "/x/calc", "/x", { name: "Mon test" });
assert.equal(lldb.type, "lldb");
assert.equal(lldb.name, "Mon test");
assert.equal(lldb.cwd, "/x");
assert.equal(lldb.stopOnEntry, false);

// F11 n'entre pas dans le runtime : lldb (avant le lancement, motif sans guillemets) et gdb ; sauf si on le demande.
assert.equal(STEP_AVOID_PATTERN, "^(rgl::standard::|std::|__gnu_cxx::|_LIBCPP)");
assert.deepEqual(lldb.initCommands, ["settings set target.process.thread.step-avoid-regexp ^(rgl::standard::|std::|__gnu_cxx::|_LIBCPP)"]);
assert.equal(lldb.initCommands[0].includes('"'), false);
assert.equal("initCommands" in launchConfiguration("lldb", "p", "/x", { stepIntoRuntime: true }), false);
assert.ok(gdb.setupCommands.some(c => c.text.includes("skip -rfu ^(rgl::standard::")));
assert.equal(launchConfiguration("gdb", "p", "/x", { stepIntoRuntime: true }).setupCommands.length, 1);

// Le marqueur du traqueur de noms : actif par défaut, coupé par rglNames: false.
assert.equal(lldb.rglDebug, true);
assert.equal(lldb.rglNames, true);
assert.equal(vsdbg.rglDebug, true);
assert.equal(gdb.rglDebug, true);
assert.equal(launchConfiguration("lldb", "p", "/x", { rglNames: false }).rglNames, false);

// Les valeurs RGL lisibles : le script de formateurs est chargé d'abord (chemin entre guillemets, il peut avoir des espaces), puis le filtre de F11.
const formatted = launchConfiguration("lldb", "p", "/x", { formatters: "C:/ext avec espace/lldb/rgl_formatters.py" });
assert.deepEqual(formatted.initCommands, [
    'command script import "C:/ext avec espace/lldb/rgl_formatters.py"',
    "settings set target.process.thread.step-avoid-regexp ^(rgl::standard::|std::|__gnu_cxx::|_LIBCPP)",
]);
assert.deepEqual(launchConfiguration("lldb", "p", "/x", { formatters: "/f.py", stepIntoRuntime: true }).initCommands, ['command script import "/f.py"']);

console.log("debug : ok");
