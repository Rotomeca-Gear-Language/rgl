// « Nouveau fichier RGL… » (clic droit dans l'explorateur, juste après « Nouveau fichier ») : le type (.rgl ou .forge), le nom, et pour un .forge son genre et sa
// description ; le fichier est ensuite créé avec son en-tête et ouvert.

const path = require("path");
const vscode = require("vscode");
const { FILE_KINDS, FORGE_KINDS, FOLDER_KINDS, validateName, validateFolderName, fileContent, indexContent } = require("./new-file");

/** Le dossier où créer : celui sur lequel on a cliqué (ou le dossier du fichier cliqué), sinon le premier dossier de l'espace de travail. */
async function targetFolder(resource) {
    if (resource instanceof vscode.Uri) {
        const stat = await vscode.workspace.fs.stat(resource);
        return stat.type & vscode.FileType.Directory ? resource : vscode.Uri.file(path.dirname(resource.fsPath));
    }
    return vscode.workspace.workspaceFolders?.[0]?.uri;
}

/** Les noms des fichiers du dossier, pour refuser un fichier existant et avertir d'un deuxième .rgl. */
async function namesIn(folder) {
    try {
        return (await vscode.workspace.fs.readDirectory(folder)).map(([name]) => name);
    } catch {
        return [];
    }
}

/** @param {vscode.ExtensionContext} context */
function registerNewFile(context) {
    context.subscriptions.push(vscode.commands.registerCommand("rgl.newFile", async resource => {
        const folder = await targetFolder(resource);
        if (!folder) {
            void vscode.window.showWarningMessage("RGL : ouvrez un dossier pour y créer un fichier.");
            return;
        }

        const kind = await vscode.window.showQuickPick(FILE_KINDS.map(k => ({ label: k.label, description: k.description, extension: k.extension })),
            { title: "Nouveau fichier RGL", placeHolder: "Quel type de fichier ?" });
        if (!kind) return;

        const existing = await namesIn(folder);
        const name = await vscode.window.showInputBox({
            title: `Nouveau fichier ${kind.extension}`,
            prompt: `Nom du fichier, sans l'extension (${kind.extension}) — dans ${vscode.workspace.asRelativePath(folder, false) || path.basename(folder.fsPath)}`,
            validateInput: value => {
                const problem = validateName(value, kind.extension, { existing });
                if (!problem) return undefined;
                return { message: problem.message, severity: problem.severity === "warning" ? vscode.InputBoxValidationSeverity.Warning : vscode.InputBoxValidationSeverity.Error };
            },
        });
        if (name === undefined) return;

        let options = {};
        if (kind.extension === ".forge") {
            const forgeKind = await vscode.window.showQuickPick(FORGE_KINDS.map(k => ({ label: k.label, description: k.description, kind: k.kind })),
                { title: "Genre du fichier forge", placeHolder: "forge: quoi ? (gears, mold, train…)" });
            if (!forgeKind) return;

            const description = await vscode.window.showInputBox({
                title: "Description du fichier",
                prompt: "Ce que contient le fichier (écrit après « description: » ; peut rester vide)",
            });
            if (description === undefined) return;
            options = { forgeKind: forgeKind.kind, description };
        }

        const target = vscode.Uri.joinPath(folder, name.trim() + kind.extension);
        try {
            await vscode.workspace.fs.stat(target);
            void vscode.window.showErrorMessage(`RGL : ${path.basename(target.fsPath)} existe déjà.`);
            return;
        } catch {
            // Le fichier n'existe pas : on le crée.
        }

        await vscode.workspace.fs.writeFile(target, Buffer.from(fileContent(kind.extension, options), "utf8"));
        const document = await vscode.workspace.openTextDocument(target);
        const editor = await vscode.window.showTextDocument(document);
        // Le curseur à la fin : pour un forge, juste après l'en-tête ; pour un train, avant le train par défaut.
        const last = document.lineAt(Math.max(0, document.lineCount - 1));
        const end = options.forgeKind === "train" ? new vscode.Position(Math.max(0, document.lineCount - 3), 0) : last.range.end;
        editor.selection = new vscode.Selection(end, end);
    }));
}

/**
 * Un dossier spécial : on demande son nom (sans suffixe), puis on crée `Nom.box` ou `Nom.tools` dans le dossier cliqué. Une box reçoit son `index.forge`,
 * que l'on ouvre ; un dossier d'outils reste vide et se montre dans l'explorateur.
 * @param {"box" | "tools"} which
 */
async function createSpecialFolder(which, resource) {
    const folder = await targetFolder(resource);
    if (!folder) {
        void vscode.window.showWarningMessage("RGL : ouvrez un dossier pour y créer un dossier.");
        return;
    }

    const kind = FOLDER_KINDS[which];
    const existing = await namesIn(folder);
    const name = await vscode.window.showInputBox({
        title: `Nouveau dossier ${kind.label}`,
        prompt: kind.prompt,
        validateInput: value => {
            const problem = validateFolderName(value, kind.suffix, { existing });
            return problem ? { message: problem.message, severity: vscode.InputBoxValidationSeverity.Error } : undefined;
        },
    });
    if (name === undefined) return;

    let description;
    if (which === "box") {
        description = await vscode.window.showInputBox({ title: "Description de la box", prompt: "Ce que contient la box (écrit dans son index.forge ; peut rester vide)" });
        if (description === undefined) return;
    }

    const target = vscode.Uri.joinPath(folder, name.trim() + kind.suffix);
    await vscode.workspace.fs.createDirectory(target);
    if (which === "tools") {
        await vscode.commands.executeCommand("revealInExplorer", target);
        return;
    }

    const index = vscode.Uri.joinPath(target, "index.forge");
    await vscode.workspace.fs.writeFile(index, Buffer.from(indexContent(description), "utf8"));
    const document = await vscode.workspace.openTextDocument(index);
    const editor = await vscode.window.showTextDocument(document);
    // Le curseur sous l'aide, là où s'écrivent les imports.
    const end = document.lineAt(Math.max(0, document.lineCount - 1)).range.end;
    editor.selection = new vscode.Selection(end, end);
}

/** @param {vscode.ExtensionContext} context */
function registerNewFolders(context) {
    context.subscriptions.push(
        vscode.commands.registerCommand("rgl.newBox", resource => createSpecialFolder("box", resource)),
        vscode.commands.registerCommand("rgl.newTools", resource => createSpecialFolder("tools", resource)));
}

module.exports = { registerNewFile, registerNewFolders };
