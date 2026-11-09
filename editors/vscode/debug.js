"use strict";
// Le débogage d'un programme RGL. Le compilateur écrit des directives `#line` dans le C++ et compile avec `-g` en debug : un débogueur C++ (gdb ou
// LLDB) lit donc les lignes et les fichiers du `.rgl` lui-même, sans adaptateur de débogage de plus. Ce fichier ne dépend pas de VS Code : il trouve le
// projet et l'exécutable, choisit le débogueur et écrit la configuration ; extension.js compile, puis lance la session.

const fs = require("fs");
const path = require("path");

const TEST_SUFFIX = ".test.rgl";

/**
 * Les fonctions où F11 (entrer dans) n'entre pas : le runtime de RGL (les constructeurs de `string`, `symbol`…, les tableaux) et la bibliothèque C++. Sans ce filtre,
 * une ligne comme `string copie: texte;` envoie dans `string.hpp`. Écrit sans espace ni guillemets : lldb prendrait les guillemets pour une partie du motif, et le
 * réglage ne vaut que pour les threads créés après lui (il est donc posé avant le lancement).
 */
const STEP_AVOID_PATTERN = "^(rgl::standard::|std::|__gnu_cxx::|_LIBCPP)";

/**
 * Le projet d'un fichier : le dossier du `rgl.json` le plus proche en remontant.
 * @param {string} start Un fichier ou un dossier.
 * @param {Pick<typeof fs, "existsSync" | "readFileSync" | "statSync">} [files]
 * @returns {{ root: string, name?: string } | undefined}
 */
function findProject(start, files = fs) {
    let directory = files.existsSync(start) && files.statSync(start).isDirectory() ? start : path.dirname(start);
    for (;;) {
        const manifest = path.join(directory, "rgl.json");
        if (files.existsSync(manifest)) {
            let name;
            try {
                const json = JSON.parse(files.readFileSync(manifest, "utf8"));
                if (typeof json.name === "string" && json.name.length > 0) name = json.name;
            } catch {
                // Un manifeste illisible : le compilateur le dira ; le nom retombe sur celui du fichier d'entrée.
            }
            return { root: directory, name };
        }
        const parent = path.dirname(directory);
        if (parent === directory) return undefined;
        directory = parent;
    }
}

/**
 * Le fichier à compiler : le fichier actif s'il est du RGL, sinon l'unique `.rgl` (hors fichiers de test) de la racine du projet.
 * @returns {string | undefined}
 */
function entryFile(project, activeFile, files = fs) {
    if (activeFile && /\.rgl$/i.test(activeFile)) return activeFile;
    let candidates;
    try {
        candidates = files.readdirSync(project.root).filter(f => /\.rgl$/i.test(f) && !f.toLowerCase().endsWith(TEST_SUFFIX));
    } catch {
        return undefined;
    }
    return candidates.length === 1 ? path.join(project.root, candidates[0]) : undefined;
}

/**
 * Le fichier `.rgl` à compiler, lancer ou déboguer quand on est dans un fichier quelconque du projet : un `.rgl` est lui-même, un `.forge`, un `.paper` ou tout
 * autre fichier du projet renvoie au `.rgl` d'entrée de son projet (le seul `.rgl` de la racine, hors fichiers de test).
 * @param {string} file Le fichier actif.
 * @returns {{ entry?: string, project?: { root: string, name?: string }, reason?: string }} `reason` dit pourquoi aucun fichier n'est trouvé.
 */
function projectEntryFor(file, files = fs) {
    const project = findProject(file, files);
    if (/\.rgl$/i.test(file)) return { entry: file, project };
    if (!project) return { reason: "aucun rgl.json trouvé au-dessus de ce fichier : ouvrez un fichier d'un projet RGL" };

    const entry = entryFile(project, undefined, files);
    return entry ? { entry, project } : { project, reason: "le projet n'a pas un seul fichier .rgl (hors fichiers de test) : ouvrez celui à lancer" };
}

/** Le nom du projet compilé : celui du manifeste, sinon le fichier d'entrée ; un fichier de test a le sien (`projet-calcul`). */
function projectName(project, entry) {
    const base = path.basename(entry);
    if (base.toLowerCase().endsWith(TEST_SUFFIX)) return `${project.name ?? "tests"}-${base.slice(0, -TEST_SUFFIX.length)}`;
    return project.name ?? base.replace(/\.rgl$/i, "");
}

/** L'exécutable qu'écrit `rgl compile` : `<projet>/out/<profil>/<nom>`, avec `.exe` sous Windows. */
function executablePath(project, entry, profile = "debug", platform = process.platform) {
    return path.join(project.root, "out", profile, projectName(project, entry) + (platform === "win32" ? ".exe" : ""));
}

/**
 * Le débogueur à utiliser. **Sous Windows, Zig écrit les informations de débogage dans un `.pdb` (CodeView), pas en DWARF** : gdb n'y voit aucune source, aucun
 * point d'arrêt ne se pose. Il faut un débogueur qui lit les PDB : LLDB (extension CodeLLDB, vérifié : un point d'arrêt dans un train s'arrête) ou le débogueur
 * de Visual Studio (`cppvsdbg`, extension C/C++). Ailleurs (ELF, DWARF), gdb convient.
 * @param {"auto" | "gdb" | "lldb" | "vsdbg" | undefined} requested Le réglage `debugger` de la configuration.
 * @param {ReadonlySet<string>} installed Les identifiants des extensions installées (minuscules).
 * @param {string} [platform] `process.platform` par défaut.
 * @returns {{ kind: "gdb" | "lldb" | "vsdbg" } | { error: string }}
 */
function chooseDebugger(requested, installed, platform = process.platform) {
    const cpptools = installed.has("ms-vscode.cpptools");
    const codelldb = installed.has("vadimcn.vscode-lldb");
    const windows = platform === "win32";
    const needCpptools = "l'extension « C/C++ » (ms-vscode.cpptools) n'est pas installée";
    const needCodelldb = "l'extension « CodeLLDB » (vadimcn.vscode-lldb) n'est pas installée";

    if (requested === "gdb") {
        if (windows) return { error: "gdb ne lit pas les informations de débogage de Windows (PDB) : choisissez « lldb » (CodeLLDB) ou « vsdbg » (extension C/C++)" };
        return cpptools ? { kind: "gdb" } : { error: needCpptools + " : elle fournit le débogueur gdb" };
    }
    if (requested === "vsdbg") {
        if (!windows) return { error: "« vsdbg » (le débogueur de Visual Studio) n'existe que sous Windows : choisissez « gdb » ou « lldb »" };
        return cpptools ? { kind: "vsdbg" } : { error: needCpptools };
    }
    if (requested === "lldb") return codelldb ? { kind: "lldb" } : { error: needCodelldb };

    if (windows) {
        if (codelldb) return { kind: "lldb" };
        if (cpptools) return { kind: "vsdbg" };
        return { error: "aucun débogueur : installez l'extension « CodeLLDB » ou « C/C++ » (qui apporte le débogueur de Visual Studio)" };
    }
    if (cpptools) return { kind: "gdb" };
    if (codelldb) return { kind: "lldb" };
    return { error: "aucun débogueur : installez l'extension « C/C++ » (gdb) ou « CodeLLDB »" };
}

/**
 * La configuration de débogage C++ qui lance l'exécutable.
 * @param {{ name?: string, args?: string[], cwd?: string, env?: Record<string, string>, stopAtEntry?: boolean, debuggerPath?: string, stepIntoRuntime?: boolean, rglNames?: boolean, formatters?: string }} options
 */
function launchConfiguration(kind, program, root, options = {}) {
    const name = options.name ?? "Déboguer le programme RGL";
    const cwd = options.cwd ?? root;
    const args = options.args ?? [];
    const env = options.env ?? {};
    const avoid = options.stepIntoRuntime !== true;
    // rglDebug : la configuration est celle d'un programme RGL, dont le traqueur de noms réécrit les variables (sauf rglNames: false).
    const marker = { rglDebug: true, rglNames: options.rglNames !== false };
    // lldb : les valeurs RGL lisibles (string, symbol, tableau) puis le filtre de F11. Le chemin est entre guillemets : il peut contenir des espaces.
    const initCommands = [
        ...(options.formatters ? [`command script import "${options.formatters}"`] : []),
        ...(avoid ? [`settings set target.process.thread.step-avoid-regexp ${STEP_AVOID_PATTERN}`] : []),
    ];
    if (kind === "lldb") {
        return {
            name, type: "lldb", request: "launch", program, args, cwd, env, stopOnEntry: options.stopAtEntry === true, terminal: "integrated",
            ...(initCommands.length > 0 ? { initCommands } : {}),
            ...marker,
        };
    }
    if (kind === "vsdbg") {
        return {
            name,
            type: "cppvsdbg",
            request: "launch",
            program,
            args,
            cwd,
            environment: Object.entries(env).map(([key, value]) => ({ name: key, value })),
            stopAtEntry: options.stopAtEntry === true,
            console: "integratedTerminal",
            ...marker,
        };
    }
    return {
        name,
        type: "cppdbg",
        request: "launch",
        program,
        args,
        cwd,
        environment: Object.entries(env).map(([key, value]) => ({ name: key, value })),
        stopAtEntry: options.stopAtEntry === true,
        externalConsole: false,
        MIMode: "gdb",
        ...(options.debuggerPath ? { miDebuggerPath: options.debuggerPath } : {}),
        setupCommands: [
            { description: "Affichage lisible des valeurs", text: "-enable-pretty-printing", ignoreFailures: true },
            ...(avoid ? [{ description: "F11 n'entre pas dans le runtime", text: `-interpreter-exec console "skip -rfu ${STEP_AVOID_PATTERN}"`, ignoreFailures: true }] : []),
        ],
        ...marker,
    };
}

module.exports = { findProject, entryFile, projectEntryFor, projectName, executablePath, chooseDebugger, launchConfiguration, STEP_AVOID_PATTERN, TEST_SUFFIX };
