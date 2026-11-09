"use strict";
// Les tâches RGL : compiler, exécuter et vérifier le projet depuis l'éditeur. Ce fichier ne dépend pas de VS Code : il dit seulement quelle
// ligne de commande lancer ; extension.js en fait des tâches et des commandes.

/** Les commandes que l'extension sait lancer, avec leur titre. */
const COMMANDS = {
    compile: "Compiler",
    exec: "Exécuter",
    check: "Vérifier",
};

/**
 * La ligne de commande d'une tâche.
 * @param {{ command: "compile" | "exec" | "check", release?: boolean, file?: string, folder?: string }} definition
 * @param {string} compiler Le chemin du compilateur réglé (`rgl`, un chemin, ou un `.dll` lancé avec `dotnet`).
 * @returns {{ command: string, args: string[] }}
 */
function commandLine(definition, compiler) {
    if (!(definition.command in COMMANDS)) throw new Error(`commande RGL inconnue : ${definition.command}`);

    const launch = compiler.toLowerCase().endsWith(".dll") ? { command: "dotnet", args: [compiler] } : { command: compiler, args: [] };
    const args = [...launch.args, definition.command];
    if (definition.file) args.push(definition.file);
    else if (definition.folder) args.push("-d", definition.folder);
    if (definition.release && definition.command !== "check") args.push("--release");
    return { command: launch.command, args };
}

/** Le titre d'une tâche : « Compiler », « Exécuter (release) »… */
function title(definition) {
    return COMMANDS[definition.command] + (definition.release && definition.command !== "check" ? " (release)" : "");
}

/** Les tâches proposées sans configuration : chaque commande, et compiler et exécuter en release. */
function defaultDefinitions() {
    return [
        { command: "compile" },
        { command: "exec" },
        { command: "check" },
        { command: "compile", release: true },
        { command: "exec", release: true },
    ];
}

module.exports = { COMMANDS, commandLine, title, defaultDefinitions };
