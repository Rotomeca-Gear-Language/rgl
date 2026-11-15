"use strict";
// Les commandes de paquets de la palette : ajouter, retirer, récupérer, mettre à jour, afficher le graphe. Chacune lance la commande `rgl` correspondante comme une
// tâche (sortie dans le terminal, diagnostics repérés par `$rgl`) ; l'action rapide « rgl restore » du serveur de langage appelle `rgl.restore`.

const fs = require("fs");
const path = require("path");
const vscode = require("vscode");
const { validateAddress, dependenciesOf } = require("./packages");

/** Le dossier de projet d'une commande : celui du fichier actif, sinon le premier dossier ouvert. */
function projectFolder() {
    const active = vscode.window.activeTextEditor?.document;
    const folder = (active && vscode.workspace.getWorkspaceFolder(active.uri)) ?? vscode.workspace.workspaceFolders?.[0];
    if (!folder) void vscode.window.showWarningMessage("RGL : ouvrez un dossier de projet pour gérer ses paquets.");
    return folder;
}

/** Les dépendances du `rgl.json` du dossier. */
function currentDependencies(folder) {
    try {
        return dependenciesOf(fs.readFileSync(path.join(folder.uri.fsPath, "rgl.json"), "utf8"));
    } catch {
        return [];
    }
}

/**
 * @param {vscode.ExtensionContext} context
 * @param {(definition: object, folder: vscode.WorkspaceFolder) => Promise<void>} run Lance une tâche `rgl` (voir extension.js).
 */
function registerPackages(context, run) {
    const simple = (command, definition) => async () => {
        const folder = projectFolder();
        if (folder) await run({ command, ...definition }, folder);
    };

    context.subscriptions.push(
        vscode.commands.registerCommand("rgl.addPackage", async () => {
            const folder = projectFolder();
            if (!folder) return;
            const address = await vscode.window.showInputBox({
                title: "Ajouter un paquet",
                prompt: "Adresse du dépôt, avec une version si tu la veux : github.com/alice/json ou github.com/alice/json@1.2.0 (la dernière sans version)",
                placeHolder: "github.com/alice/json",
                validateInput: value => validateAddress(value) ?? undefined,
            });
            if (address) await run({ command: "add", args: [address.trim()] }, folder);
        }),
        vscode.commands.registerCommand("rgl.removePackage", async () => {
            const folder = projectFolder();
            if (!folder) return;
            const dependencies = currentDependencies(folder);
            if (dependencies.length === 0) {
                void vscode.window.showInformationMessage("RGL : ce projet n'a aucune dépendance.");
                return;
            }
            const picked = await vscode.window.showQuickPick(dependencies.map(d => ({ label: d.address, description: d.version })), { title: "Retirer un paquet" });
            if (picked) await run({ command: "remove", args: [picked.label] }, folder);
        }),
        vscode.commands.registerCommand("rgl.restore", simple("restore")),
        vscode.commands.registerCommand("rgl.packageTree", simple("tree")),
        vscode.commands.registerCommand("rgl.outdatedPackages", simple("outdated")),
        vscode.commands.registerCommand("rgl.updatePackages", simple("update")),
        vscode.commands.registerCommand("rgl.publishPackage", async () => {
            const folder = projectFolder();
            if (!folder) return;
            const dry = await vscode.window.showQuickPick(
                [{ label: "Vérifier seulement", description: "rgl publish --dry-run : rien n'est créé", dry: true },
                 { label: "Publier", description: "crée le tag de la version et le pousse vers origin", dry: false }],
                { title: "Publier ce paquet" });
            if (dry) await run({ command: "publish", args: dry.dry ? ["--dry-run"] : [] }, folder);
        }));

    // Quand les paquets changent (manifeste, lock), le serveur de langage relit : rien à faire ici, il surveille rgl.json et rgl.lock.
}

module.exports = { registerPackages };
