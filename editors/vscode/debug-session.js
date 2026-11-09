// Débogage d'un programme RGL : F5 compile le projet en debug (`-g`, positions du `.rgl` dans les informations de débogage), puis lance l'exécutable
// sous gdb (extension « C/C++ ») ou LLDB (extension « CodeLLDB »). Les points d'arrêt se posent dans les fichiers `.rgl`.

const fs = require("fs");
const path = require("path");
const { spawn } = require("child_process");
const vscode = require("vscode");
const { commandLine } = require("./tasks");
const { findProject, entryFile, executablePath, chooseDebugger, launchConfiguration } = require("./debug");
const { NameTracker, expressionAt } = require("./debug-names");
const { findRegions, reactiveNames } = require("./paper-regions");

/** @type {vscode.OutputChannel | undefined} */
let channel;

/** Compile `entry` en debug ; rend le code de sortie du compilateur. */
function compile(compiler, project, entry) {
    channel ??= vscode.window.createOutputChannel("RGL Débogage");
    const { command, args } = commandLine({ command: "compile", file: entry }, compiler);
    channel.show(true);
    channel.appendLine(`> ${command} ${args.join(" ")}`);
    return new Promise(resolve => {
        const child = spawn(command, args, { cwd: project.root, env: { ...process.env, NO_COLOR: "1" } });
        child.stdout.on("data", chunk => channel.append(chunk.toString()));
        child.stderr.on("data", chunk => channel.append(chunk.toString()));
        child.on("error", error => {
            channel.appendLine(`impossible de lancer le compilateur : ${error.message}`);
            resolve(-1);
        });
        child.on("close", code => resolve(code ?? -1));
    });
}

/** Le fichier d'où partir : celui de la configuration (relatif au dossier), sinon le document actif. */
function startingPoint(config, folder) {
    if (typeof config.file === "string" && config.file.length > 0) return path.resolve(folder?.uri.fsPath ?? "", config.file);
    const active = vscode.window.activeTextEditor?.document.uri;
    if (active?.scheme === "file") return active.fsPath;
    return folder?.uri.fsPath;
}

/**
 * Prépare et lance la session : compile, choisit le débogueur, démarre la configuration C++.
 * Rend `undefined` : la session lancée est celle du débogueur C++, pas celle de la configuration `rgl`.
 */
async function startSession(config, folder, compiler, extensionPath) {
    const fail = message => {
        void vscode.window.showErrorMessage(`RGL : ${message}`);
        return undefined;
    };

    const start = startingPoint(config, folder);
    if (!start) return fail("ouvrez un fichier ou un dossier de projet RGL pour le déboguer.");
    const project = findProject(start);
    if (!project) return fail("aucun rgl.json trouvé au-dessus de ce fichier.");

    const active = vscode.window.activeTextEditor?.document;
    if (active?.isDirty) await active.save();

    const entry = entryFile(project, /\.rgl$/i.test(start) ? start : undefined);
    if (!entry && !config.program) return fail("le projet n'a pas un seul fichier .rgl : indiquez « file » dans la configuration.");

    const installed = new Set(vscode.extensions.all.map(extension => extension.id.toLowerCase()));
    const chosen = chooseDebugger(config.debugger, installed);
    if ("error" in chosen) return fail(chosen.error);

    if (!config.program && config.build !== false) {
        const code = await vscode.window.withProgress({ location: vscode.ProgressLocation.Notification, title: "RGL : compilation en debug…" }, () => compile(compiler, project, entry));
        if (code !== 0) return fail("la compilation a échoué (voir le panneau « RGL Débogage »).");
    }

    const program = config.program ? path.resolve(project.root, config.program) : executablePath(project, entry);
    if (!fs.existsSync(program)) return fail(`l'exécutable est introuvable : ${program}`);

    const launch = launchConfiguration(chosen.kind, program, project.root, {
        name: config.name,
        args: config.args,
        cwd: config.cwd ? path.resolve(project.root, config.cwd) : undefined,
        env: config.env,
        stopAtEntry: config.stopAtEntry,
        debuggerPath: config.debuggerPath,
        stepIntoRuntime: config.stepIntoRuntime,
        rglNames: config.rglNames,
        // Les valeurs RGL lisibles (string, symbol, tableau) : un script lldb de l'extension, sauf rglValues: false.
        formatters: config.rglValues === false ? undefined : path.join(extensionPath, "lldb", "rgl_formatters.py"),
    });
    await vscode.debug.startDebugging(folder, launch);
    return undefined;
}

/** @param {vscode.ExtensionContext} context @param {() => string} compilerPath */
function registerDebugging(context, compilerPath) {
    const provider = {
        provideDebugConfigurations: () => [{ type: "rgl", request: "launch", name: "Déboguer le programme RGL" }],
        resolveDebugConfiguration: (folder, config) => startSession(config, folder, compilerPath(), context.extensionPath),
    };

    /** Les états `reactive` d'une page : son texte tel que l'éditeur le montre (enregistré ou non), sinon celui du disque. */
    const reactivesOf = file => {
        try {
            const open = vscode.workspace.textDocuments.find(document => document.fileName.toLowerCase() === file.toLowerCase());
            return reactiveNames(open ? open.getText() : fs.readFileSync(file, "utf8"));
        } catch {
            return [];
        }
    };

    /**
     * Les noms du cadre du haut de la pile : le panneau Variables les demande, mais il peut être fermé, et alors rien ne les connaît au premier survol.
     * Les réponses repassent par le traqueur, qui retient les noms.
     */
    const prefetch = async (session, frameId) => {
        try {
            const scopes = await session.customRequest("scopes", { frameId });
            // Les locales, mais aussi les constantes de fichier (portées Static et Global) : pas les registres.
            for (const scope of scopes?.scopes ?? []) {
                if (!/regist/i.test(scope.name)) await session.customRequest("variables", { variablesReference: scope.variablesReference });
            }
        } catch {
            // Le débogueur n'est plus arrêté : tant pis, le panneau Variables fera la demande.
        }
    };
    context.subscriptions.push(
        // Dans un cadre écrit en RGL, les variables portent leurs noms RGL ; dans un cadre C++, les noms C++ restent.
        vscode.debug.registerDebugAdapterTrackerFactory("*", {
            createDebugAdapterTracker(session) {
                if (session.configuration?.rglDebug !== true || session.configuration.rglNames === false) return undefined;
                const tracker = new NameTracker({ prefetch: frameId => void prefetch(session, frameId), reactives: reactivesOf });
                return { onWillReceiveMessage: message => tracker.onWillReceiveMessage(message), onDidSendMessage: message => tracker.onDidSendMessage(message) };
            },
        }),
        // Le survol d'une variable, pendant le débogage, donne sa valeur : dans un .rgl, un .forge et dans le RGL d'une page .paper (blocs et interpolations).
        vscode.languages.registerEvaluatableExpressionProvider([{ language: "rgl" }, { pattern: "**/*.paper" }], {
            provideEvaluatableExpression(document, position) {
                if (/\.paper$/i.test(document.fileName)) {
                    const offset = document.offsetAt(position);
                    if (!findRegions(document.getText()).some(region => region.start <= offset && offset <= region.end)) return undefined;
                }
                const found = expressionAt(document.lineAt(position.line).text, position.character);
                if (!found) return undefined;
                return new vscode.EvaluatableExpression(new vscode.Range(position.line, found.start, position.line, found.end), found.text);
            },
        }),
        vscode.debug.registerDebugConfigurationProvider("rgl", provider, vscode.DebugConfigurationProviderTriggerKind.Initial),
        vscode.debug.registerDebugConfigurationProvider("rgl", provider, vscode.DebugConfigurationProviderTriggerKind.Dynamic),
        vscode.commands.registerCommand("rgl.debug", () => vscode.debug.startDebugging(vscode.workspace.workspaceFolders?.[0],
            { type: "rgl", request: "launch", name: "Déboguer le programme RGL" })));
}

module.exports = { registerDebugging };
