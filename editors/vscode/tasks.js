"use strict";
// Les tâches RGL : compiler, exécuter et vérifier le projet depuis l'éditeur. Ce fichier ne dépend pas de VS Code : il dit seulement quelle
// ligne de commande lancer ; extension.js en fait des tâches et des commandes.

/** Les commandes que l'extension sait lancer, avec leur titre. */
const COMMANDS = {
    compile: "Compiler",
    exec: "Exécuter",
    check: "Vérifier",
    add: "Ajouter un paquet",
    remove: "Retirer un paquet",
    restore: "Récupérer les paquets",
    tree: "Graphe des paquets",
    outdated: "Paquets à mettre à jour",
    update: "Mettre à jour les paquets",
    publish: "Publier le paquet",
};

/** Les commandes de paquets : elles travaillent sur le `rgl.json` du dossier, sans fichier d'entrée ni profil. */
const PACKAGE_COMMANDS = ["add", "remove", "restore", "tree", "outdated", "update", "publish"];

/**
 * La ligne de commande d'une tâche.
 * @param {{ command: string, release?: boolean, file?: string, folder?: string, args?: string[] }} definition `args` : les arguments d'une commande de paquets (une adresse, `--dry-run`).
 * @param {string} compiler Le chemin du compilateur réglé (`rgl`, un chemin, ou un `.dll` lancé avec `dotnet`).
 * @returns {{ command: string, args: string[] }}
 */
function commandLine(definition, compiler) {
    if (!(definition.command in COMMANDS)) throw new Error(`commande RGL inconnue : ${definition.command}`);

    const launch = compiler.toLowerCase().endsWith(".dll") ? { command: "dotnet", args: [compiler] } : { command: compiler, args: [] };
    const args = [...launch.args, definition.command];
    if (PACKAGE_COMMANDS.includes(definition.command)) {
        args.push(...(definition.args ?? []));
        if (definition.folder) args.push("-d", definition.folder);
        return { command: launch.command, args };
    }
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

module.exports = { COMMANDS, PACKAGE_COMMANDS, commandLine, title, defaultDefinitions };
