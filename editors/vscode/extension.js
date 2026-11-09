// Extension RGL : lance le compilateur en serveur de langage (`rgl lsp`), qui publie les
// diagnostics des fichiers ouverts, propose l'autocomplétion (avec import automatique), décrit ce
// qu'on survole, aide à saisir les appels et mène aux définitions. La coloration et les snippets
// sont déclarés dans package.json et ne dépendent pas du serveur.

const fs = require("fs");
const path = require("path");
const vscode = require("vscode");
const { LanguageClient } = require("vscode-languageclient/node");
const { DatasetLensProvider, openDatasetEditor } = require("./dataset-editor");
const { standardLibraryRecovery } = require("./standard-library");
const { commandLine, title, defaultDefinitions, COMMANDS } = require("./tasks");
const { registerDebugging } = require("./debug-session");
const { projectEntryFor } = require("./debug");
const { registerTests } = require("./test-explorer");
const { registerPaperDecorations } = require("./paper-decorations");
const { registerMoldSuggestion } = require("./mold-suggestion");
const { registerNewFile, registerNewFolders } = require("./new-file-command");

const recoverStandardLibrary = standardLibraryRecovery({
    prompt: () => vscode.window.showWarningMessage(
        "RGL : la bibliothèque standard est absente ou incomplète dans le cache. La restaurer pour activer l’analyse ?",
        "Restaurer la std"),
    restore: () => client.sendRequest("rgl/standardLibrary", { restore: true }),
    restart: async () => { await stop(); await start(); },
    reportError: message => vscode.window.showErrorMessage(`RGL : ${message}`),
});

/** @type {LanguageClient | undefined} */
let client;

/** @type {vscode.StatusBarItem | undefined} */
let status;

/** Le dossier de l'extension où le compilateur est copié avant d'être lancé. */
let storage = "";
let nativeWatcher;

/** Le compilateur réglé par `rgl.compilerPath` : `rgl` par défaut. */
function compilerPath() {
    return vscode.workspace.getConfiguration("rgl").get("compilerPath") || "rgl";
}

const { serverOptions } = require("./server-launch");

/** Vrai si le compilateur existe : un chemin, ou un nom trouvé dans le PATH (avec les extensions de PATHEXT sous Windows). */
function exists(compiler) {
    if (isPath(compiler)) return fs.existsSync(compiler);

    const extensions = process.platform === "win32" ? (process.env.PATHEXT || ".EXE;.CMD;.BAT").split(";") : [""];
    return (process.env.PATH || "").split(path.delimiter).some(directory =>
        extensions.some(extension => fs.existsSync(path.join(directory, compiler + extension))));
}

function isPath(compiler) {
    return compiler.includes("/") || compiler.includes("\\");
}

/**
 * Le compilateur à lancer : un chemin réglé est d'abord copié avec son dossier dans celui de
 * l'extension. Lancé sur place, le serveur verrouillerait ses fichiers, et la compilation du
 * compilateur (dotnet build) ne pourrait plus les remplacer. Chaque démarrage a sa copie ; les
 * anciennes sont effacées quand plus rien ne les tient.
 */
function shadowCopy(compiler) {
    if (!isPath(compiler) || !storage) return compiler;

    for (const old of fs.existsSync(storage) ? fs.readdirSync(storage) : []) {
        try {
            fs.rmSync(path.join(storage, old), { recursive: true, force: true });
        } catch {
            // Encore tenue par un serveur qui s'arrête : effacée au prochain démarrage.
        }
    }

    const copy = path.join(storage, `serveur-${Date.now()}`);
    fs.cpSync(path.dirname(compiler), copy, { recursive: true });
    return path.join(copy, path.basename(compiler));
}

/** Signale que le serveur ne démarre pas, et propose d'ouvrir le réglage. */
async function reportFailure(reason) {
    const choice = await vscode.window.showErrorMessage(
        `RGL : le serveur de langage ne démarre pas (${reason}). Réglez « rgl.compilerPath » sur rgl.exe ou rgl.dll.`,
        "Ouvrir le réglage");
    if (choice) await vscode.commands.executeCommand("workbench.action.openSettings", "rgl.compilerPath");
}

async function start() {
    // Vérifié avant de créer le client : il afficherait sinon sa propre erreur, en plus de celle-ci.
    const compiler = compilerPath();
    if (!exists(compiler)) {
        await reportFailure(`« ${compiler} » introuvable`);
        return;
    }

    try {
        client = new LanguageClient("rgl", "RGL", serverOptions(shadowCopy(compiler), compiler), {
            // Les pages .paper (du HTML) : le serveur lit le RGL de leurs blocs @rgl et de leurs interpolations @@ … @@.
            documentSelector: [{ language: "rgl" }, { pattern: "**/*.paper" }],
            initializationOptions: { manageStandardLibrary: true },
            middleware: {
                handleDiagnostics(uri, diagnostics, next) {
                    next(uri, diagnostics);
                    if (diagnostics.some(d => d.code === "RGL001")) void recoverStandardLibrary();
                },
            },
            synchronize: { fileEvents: nativeWatcher },
        });
        await client.start();
        showServer(compiler);
        if (client.initializeResult?.capabilities?.experimental?.standardLibraryRecovery) {
            const standard = await client.sendRequest("rgl/standardLibrary", {});
            if (!standard.available) void recoverStandardLibrary();
        }
    } catch (error) {
        client = undefined;
        await reportFailure(error.message ?? error);
    }
}

/** Montre dans la barre d'état quel serveur répond (version, date de compilation) ; un clic le relance. */
function showServer(compiler) {
    const info = client?.initializeResult?.serverInfo;
    if (!status) {
        status = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Right, 0);
        status.command = "rgl.restartServer";
    }
    status.text = `$(symbol-misc) RGL ${info?.version ?? ""}`.trim();
    status.tooltip = `Serveur de langage RGL
${compiler}
Cliquer pour le redémarrer.`;
    status.show();
    client?.outputChannel.appendLine(`Serveur RGL : ${info?.version ?? "?"} (${compiler})`);
}

async function stop() {
    if (status) status.hide();
    if (!client) return;
    const running = client;
    client = undefined;
    await running.stop();
}

/**
 * Journal du côté éditeur : ce que VS Code détient réellement comme diagnostics pour chaque fichier RGL après chaque publication du serveur. Il
 * départage « le serveur n'envoie rien » et « l'éditeur ne montre rien » (panneau Sortie, canal « RGL · diagnostics »).
 */
function traceDiagnostics(context) {
    const channel = vscode.window.createOutputChannel("RGL · diagnostics");
    context.subscriptions.push(channel, vscode.languages.onDidChangeDiagnostics(event => {
        for (const uri of event.uris) {
            if (!/.(rgl|forge)$/i.test(uri.path)) continue;
            const all = vscode.languages.getDiagnostics(uri);
            const bySource = {};
            for (const diagnostic of all) bySource[diagnostic.source ?? "?"] = (bySource[diagnostic.source ?? "?"] ?? 0) + 1;
            channel.appendLine(`${new Date().toLocaleTimeString()}  ${path.basename(uri.fsPath)} : ${all.length} diagnostic(s) ${JSON.stringify(bySource)}`);
            for (const diagnostic of all.slice(0, 12)) {
                const r = diagnostic.range;
                channel.appendLine(`      ${diagnostic.code ?? "-"} ligne ${r.start.line + 1}:${r.start.character + 1}-${r.end.line + 1}:${r.end.character + 1} ${diagnostic.message.split("\n")[0]}`);
            }
        }
    }));
}

/**
 * Une tâche `rgl` : le compilateur lancé dans le dossier du projet. `NO_COLOR` garde la sortie sans couleur pour que le détecteur de problèmes
 * (`$rgl`) lise les diagnostics.
 */
function createTask(definition, folder) {
    const { command, args } = commandLine(definition, compilerPath());
    const execution = new vscode.ShellExecution(command, args, { cwd: folder.uri.fsPath, env: { NO_COLOR: "1" } });
    const task = new vscode.Task({ type: "rgl", ...definition }, folder, title(definition), "rgl", execution, "$rgl");
    if (definition.command === "compile") task.group = vscode.TaskGroup.Build;
    if (definition.command === "check") task.group = vscode.TaskGroup.Test;
    task.presentationOptions = { reveal: vscode.TaskRevealKind.Always, clear: true, panel: vscode.TaskPanelKind.Shared };
    return task;
}

const taskProvider = {
    provideTasks() {
        return (vscode.workspace.workspaceFolders ?? []).flatMap(folder => defaultDefinitions().map(definition => createTask(definition, folder)));
    },
    resolveTask(task) {
        const definition = task.definition;
        const folder = task.scope && task.scope.uri ? task.scope : vscode.workspace.workspaceFolders?.[0];
        return definition.command in COMMANDS && folder ? createTask(definition, folder) : undefined;
    },
};

/** Lance une commande RGL sur le fichier actif s'il est du RGL, sinon sur le projet du dossier ouvert. */
async function runCommand(command, release = false) {
    const active = vscode.window.activeTextEditor?.document;
    const folder = (active && vscode.workspace.getWorkspaceFolder(active.uri)) ?? vscode.workspace.workspaceFolders?.[0];
    if (!folder) {
        void vscode.window.showWarningMessage("RGL : ouvrez un dossier de projet pour lancer cette commande.");
        return;
    }
    if (active?.isDirty) await active.save();

    // Dans un .rgl : lui-même ; dans un .forge, un .paper ou tout autre fichier du projet : le .rgl d'entrée de son projet.
    let file;
    if (active?.uri.scheme === "file") {
        const target = projectEntryFor(active.uri.fsPath);
        if (target.entry) {
            file = path.relative(folder.uri.fsPath, target.entry);
        } else {
            void vscode.window.showWarningMessage(`RGL : ${target.reason}.`);
            return;
        }
    }
    await vscode.tasks.executeTask(createTask({ command, release, file }, folder));
}

/** @param {vscode.ExtensionContext} context */
async function activate(context) {
    storage = context.globalStorageUri.fsPath;
    nativeWatcher = vscode.workspace.createFileSystemWatcher("**/{*.h,*.hpp,*.hxx,*.hh,rgl.json}");
    context.subscriptions.push(nativeWatcher);
    traceDiagnostics(context);
    context.subscriptions.push(
        vscode.commands.registerCommand("rgl.restartServer", async () => {
            await stop();
            await start();
        }),
        // L'éditeur de dataset : un tableau pour les entrées du dataset sous le curseur (CodeLens, palette, menu de l'éditeur).
        vscode.commands.registerCommand("rgl.editDataset", (uri, line) => openDatasetEditor(context, client, uri, line)),
        vscode.languages.registerCodeLensProvider({ language: "rgl" }, new DatasetLensProvider()),
        // Compiler, exécuter, vérifier : tâches, commandes de la palette et boutons de l'éditeur.
        vscode.tasks.registerTaskProvider("rgl", taskProvider),
        vscode.commands.registerCommand("rgl.build", () => runCommand("compile")),
        vscode.commands.registerCommand("rgl.run", () => runCommand("exec")),
        vscode.commands.registerCommand("rgl.runRelease", () => runCommand("exec", true)),
        vscode.commands.registerCommand("rgl.check", () => runCommand("check")),
        vscode.workspace.onDidChangeConfiguration(async event => {
            if (!event.affectsConfiguration("rgl.compilerPath")) return;
            await stop();
            await start();
        }));
    registerPaperDecorations(context);
    registerMoldSuggestion(context, () => client);
    registerNewFile(context);
    registerNewFolders(context);
    registerDebugging(context, compilerPath);
    registerTests(context, compilerPath);
    await start();
}

function deactivate() {
    return stop();
}

module.exports = { activate, deactivate };
