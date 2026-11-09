// L'explorateur de tests : chaque fichier `nom.test.rgl` du projet est un test. « Exécuter » lance `rgl exec` dessus (compilation en debug comprise) :
// le code 0 est un succès, sinon le rapport d'échec (« ÉCHEC (fichier:ligne:colonne) : raison », ou l'erreur de compilation) se pose sur sa ligne.
// « Déboguer » passe par la session de débogage RGL.

const path = require("path");
const { spawn } = require("child_process");
const vscode = require("vscode");
const { findProject } = require("./debug");
const { testName, runCommand, readFailure } = require("./testing");

const PATTERN = "**/*.test.rgl";
const EXCLUDE = "**/{out,node_modules,.git}/**";

/** Lance un test ; rend son code de sortie et tout ce qu'il a écrit. */
function execute(file, compiler, cwd, token, onOutput) {
    const { command, args } = runCommand(file, compiler);
    return new Promise(resolve => {
        let output = "";
        const child = spawn(command, args, { cwd, env: { ...process.env, NO_COLOR: "1" } });
        const take = chunk => {
            const text = chunk.toString();
            output += text;
            onOutput(text);
        };
        child.stdout.on("data", take);
        child.stderr.on("data", take);
        child.on("error", error => resolve({ code: -1, output: `impossible de lancer le compilateur : ${error.message}` }));
        child.on("close", code => resolve({ code: code ?? -1, output }));
        token.onCancellationRequested(() => child.kill());
    });
}

/** @param {vscode.ExtensionContext} context @param {() => string} compilerPath */
function registerTests(context, compilerPath) {
    const controller = vscode.tests.createTestController("rgl", "RGL");
    context.subscriptions.push(controller);

    const upsert = uri => {
        if (uri.scheme !== "file") return;
        const id = uri.toString();
        if (!controller.items.get(id)) {
            const item = controller.createTestItem(id, testName(uri.fsPath), uri);
            item.canResolveChildren = false;
            controller.items.add(item);
        }
    };
    const discover = async () => {
        for (const uri of await vscode.workspace.findFiles(PATTERN, EXCLUDE)) upsert(uri);
    };
    controller.resolveHandler = async item => {
        if (!item) await discover();
    };
    controller.refreshHandler = async () => {
        controller.items.replace([]);
        await discover();
    };

    const watcher = vscode.workspace.createFileSystemWatcher(PATTERN);
    watcher.onDidCreate(upsert);
    watcher.onDidDelete(uri => controller.items.delete(uri.toString()));
    context.subscriptions.push(watcher);

    const selected = request => {
        const all = [];
        (request.include ?? [...controller.items].map(([, item]) => item)).forEach(item => all.push(item));
        const excluded = new Set((request.exclude ?? []).map(item => item.id));
        return all.filter(item => !excluded.has(item.id));
    };

    controller.createRunProfile("Exécuter", vscode.TestRunProfileKind.Run, async (request, token) => {
        const run = controller.createTestRun(request);
        for (const item of selected(request)) {
            if (token.isCancellationRequested) break;
            const file = item.uri.fsPath;
            const root = findProject(file)?.root ?? path.dirname(file);
            run.started(item);
            const started = Date.now();
            const { code, output } = await execute(file, compilerPath(), root, token, text => run.appendOutput(text.replace(/\r?\n/g, "\r\n"), undefined, item));
            const duration = Date.now() - started;
            if (token.isCancellationRequested) {
                run.skipped(item);
            } else if (code === 0) {
                run.passed(item, duration);
            } else {
                const failure = readFailure(output);
                const message = new vscode.TestMessage(failure.message);
                if (failure.file && failure.line) {
                    const where = path.isAbsolute(failure.file) ? failure.file : path.resolve(root, failure.file);
                    message.location = new vscode.Location(vscode.Uri.file(where), new vscode.Position(failure.line - 1, Math.max(0, (failure.column ?? 1) - 1)));
                }
                run.failed(item, message, duration);
            }
        }
        run.end();
    }, true);

    controller.createRunProfile("Déboguer", vscode.TestRunProfileKind.Debug, async (request, token) => {
        const run = controller.createTestRun(request);
        for (const item of selected(request)) {
            if (token.isCancellationRequested) break;
            run.started(item);
            const folder = vscode.workspace.getWorkspaceFolder(item.uri);
            const launched = await vscode.debug.startDebugging(folder, { type: "rgl", request: "launch", name: `Test ${item.label}`, file: item.uri.fsPath });
            if (launched) run.appendOutput(`Session de débogage lancée pour ${item.label}.\r\n`, undefined, item);
            run.skipped(item);
        }
        run.end();
    }, false);

    void discover();
}

module.exports = { registerTests };
