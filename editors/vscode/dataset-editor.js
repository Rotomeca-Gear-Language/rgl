// L'éditeur de dataset : un panneau avec un tableau (une ligne par entrée, une colonne par champ du mold) qui réécrit les entrées du
// dataset sous le curseur — dans le source RGL, ou dans le fichier JSON qu'il lit (`from "moves.json"`). Les colonnes, les types et les
// valeurs possibles (variantes d'enum, entrées d'un autre dataset, booléens) viennent du serveur de langage (requête `rgl/dataset`) ;
// ce fichier ne fait que le panneau et l'écriture dans l'éditeur. Rien n'est enregistré : l'écriture est une modification du document.

"use strict";

const path = require("path");
const vscode = require("vscode");
const dataset = require("./dataset-model");

/** Le texte, avec les mots de la ligne `dataset Nom …` : ce que le CodeLens et la commande cherchent. */
const HEADER = /^\s*(?:export\s+)?dataset\s+([A-Za-z_][A-Za-z0-9_]*)\s+(?:from\s+(?:"[^"]*"|'[^']*')\s+)?(?:of|as)\s+[A-Za-z_]/;

/** @type {Map<string, EditorPanel>} */
const panels = new Map();

/** Un CodeLens « Éditer le dataset » au-dessus de chaque déclaration `dataset`. */
class DatasetLensProvider {
    provideCodeLenses(document) {
        const lenses = [];
        for (let line = 0; line < document.lineCount; ++line) {
            if (!HEADER.test(document.lineAt(line).text)) continue;
            const range = new vscode.Range(line, 0, line, 0);
            lenses.push(new vscode.CodeLens(range, { title: "$(table) Éditer le dataset", command: "rgl.editDataset", arguments: [document.uri, line] }));
        }
        return lenses;
    }
}

/**
 * Ouvre l'éditeur du dataset déclaré à la ligne donnée (celle du curseur par défaut) du document, ou le montre s'il est déjà ouvert.
 * @param {import("vscode-languageclient/node").LanguageClient | undefined} client
 * @param {vscode.Uri | undefined} uri
 * @param {number | undefined} line
 */
async function openDatasetEditor(context, client, uri, line) {
    if (!client) {
        vscode.window.showWarningMessage("RGL : le serveur de langage n'est pas démarré.");
        return;
    }

    const editor = vscode.window.activeTextEditor;
    const document = uri ? await vscode.workspace.openTextDocument(uri) : editor?.document;
    if (!document || document.languageId !== "rgl") {
        vscode.window.showInformationMessage("RGL : placez le curseur dans un fichier .rgl ou .forge qui déclare un dataset.");
        return;
    }
    const at = line ?? editor?.selection.active.line ?? 0;

    const panel = new EditorPanel(context, client, document, at);
    if (!(await panel.load())) {
        panel.dispose();
        vscode.window.showInformationMessage("RGL : aucun dataset à cette ligne. Placez le curseur sur la ligne « dataset Nom … » ou dans ses entrées.");
        return;
    }

    const key = `${document.uri.toString()}#${panel.state.name}`;
    const existing = panels.get(key);
    if (existing) {
        panel.dispose();
        existing.panel.reveal();
        await existing.refresh();
        return;
    }
    panels.set(key, panel);
    panel.panel.onDidDispose(() => panels.delete(key));
    panel.show();
}

class EditorPanel {
    constructor(context, client, document, line) {
        this.context = context;
        this.client = client;
        this.document = document;
        this.line = line;
        /** @type {any} Ce que le panneau affiche. */
        this.state = null;
        /** @type {any} La réponse du serveur : colonnes, entrées, endroit à remplacer. */
        this.data = null;
        /** @type {vscode.Uri | null} Le fichier JSON d'un `from`. */
        this.jsonUri = null;
        this.version = document.version;
        this.panel = vscode.window.createWebviewPanel("rglDataset", "Dataset", vscode.ViewColumn.Beside,
            { enableScripts: true, localResourceRoots: [vscode.Uri.joinPath(context.extensionUri, "media"), vscode.Uri.joinPath(context.extensionUri)] });
        this.panel.webview.onDidReceiveMessage(message => this.receive(message));
    }

    dispose() {
        this.panel.dispose();
    }

    /** Demande le dataset au serveur et prépare ce que le panneau affiche ; faux s'il n'y en a pas à cette ligne. */
    async load() {
        this.version = this.document.version;
        const data = await this.client.sendRequest("rgl/dataset", { textDocument: { uri: this.document.uri.toString() }, line: this.line });
        if (!data) return false;

        this.data = data;
        let message = null;
        /** @type {any[]} */
        let rows = data.rows;
        let fields = data.fields;
        let asArray = false;
        let file = null;

        if (data.source) {
            this.jsonUri = await this.findJson(data.source);
            file = this.jsonUri ? vscode.workspace.asRelativePath(this.jsonUri) : null;
            if (!this.jsonUri) {
                message = `Le fichier « ${data.source} » est introuvable dans le projet : le tableau est vide, et « Appliquer » le créera à côté de ce fichier.`;
                rows = [];
            } else {
                const text = Buffer.from(await vscode.workspace.fs.readFile(this.jsonUri)).toString("utf8");
                const read = dataset.readJson(text, data.fields);
                if ("error" in read) {
                    message = read.error;
                    rows = [];
                } else {
                    ({ rows, fields, asArray } = read);
                }
            }
        }

        this.state = {
            mode: data.source ? "json" : "inline",
            name: data.name,
            mold: data.mold,
            createsMold: data.createsMold,
            source: data.source,
            file,
            fields,
            rows,
            asArray,
            message,
        };
        this.panel.title = `Dataset ${data.name}`;
        return true;
    }

    /** Le fichier JSON d'un `from`, cherché par son nom dans le projet comme le compilateur ; l'utilisateur choisit s'il y en a plusieurs. */
    async findJson(source) {
        const base = dataset.jsonBaseName(source);
        const found = await vscode.workspace.findFiles(`**/${base}.json`, "**/{node_modules,out,.git}/**", 20);
        const exact = found.filter(uri => path.basename(uri.fsPath) === `${base}.json`);
        if (exact.length <= 1) return exact[0] ?? null;

        const choice = await vscode.window.showQuickPick(exact.map(uri => ({ label: vscode.workspace.asRelativePath(uri), uri })), { placeHolder: `Plusieurs fichiers « ${base}.json » : lequel ?` });
        return choice?.uri ?? null;
    }

    show() {
        const webview = this.panel.webview;
        const media = file => webview.asWebviewUri(vscode.Uri.joinPath(this.context.extensionUri, file));
        const nonce = Array.from({ length: 24 }, () => Math.floor(Math.random() * 36).toString(36)).join("");
        webview.html = `<!DOCTYPE html>
<html lang="fr">
<head>
<meta charset="UTF-8">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src ${webview.cspSource}; script-src 'nonce-${nonce}';">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<link rel="stylesheet" href="${media("media/dataset-editor.css")}">
<title>Dataset</title>
</head>
<body>
<div id="root"></div>
<script nonce="${nonce}" src="${media("dataset-model.js")}"></script>
<script nonce="${nonce}" src="${media("media/dataset-editor.js")}"></script>
</body>
</html>`;
    }

    post() {
        this.panel.webview.postMessage({ type: "init", state: this.state });
    }

    async refresh() {
        if (await this.load()) this.post();
    }

    async receive(message) {
        switch (message.type) {
            case "ready":
                this.post();
                break;
            case "reload":
                await this.refresh();
                break;
            case "apply":
                await this.apply(message.fields, message.rows);
                break;
        }
    }

    /** Réécrit les entrées dans l'éditeur : le corps du dataset, ou tout le fichier JSON. Le document reste à enregistrer. */
    async apply(fields, rows) {
        if (this.state.mode === "json") {
            await this.applyJson(fields, rows);
            return;
        }

        if (this.document.version !== this.version) {
            vscode.window.showWarningMessage("RGL : le document a changé depuis l'ouverture du tableau. Rechargez-le, puis refaites la modification.");
            return;
        }
        const range = this.data.body;
        if (!range) {
            vscode.window.showErrorMessage("RGL : les entrées de ce dataset sont introuvables dans le source (accolades manquantes ?).");
            return;
        }

        const edit = new vscode.WorkspaceEdit();
        edit.replace(this.document.uri,
            new vscode.Range(range.start.line, range.start.character, range.end.line, range.end.character),
            dataset.inlineBody(fields, rows, this.indentation()));
        if (!(await vscode.workspace.applyEdit(edit))) {
            vscode.window.showErrorMessage("RGL : le document n'a pas pu être modifié.");
            return;
        }
        await this.refresh();
        vscode.window.setStatusBarMessage(`RGL : entrées de « ${this.state.name} » réécrites (à enregistrer).`, 4000);
    }

    /** L'indentation des entrées : un cran de plus que la ligne du dataset, avec les espaces ou la tabulation du document. */
    indentation() {
        const header = this.document.lineAt(this.data.body ? this.data.body.start.line : this.line).text;
        const unit = vscode.window.activeTextEditor?.options.insertSpaces === false ? "\t" : " ".repeat(Number(vscode.window.activeTextEditor?.options.tabSize) || 4);
        return (header.match(/^\s*/)?.[0] ?? "") + unit;
    }

    async applyJson(fields, rows) {
        let uri = this.jsonUri;
        if (!uri) {
            const folder = path.dirname(this.document.uri.fsPath);
            uri = vscode.Uri.file(path.join(folder, `${dataset.jsonBaseName(this.state.source)}.json`));
            await vscode.workspace.fs.writeFile(uri, Buffer.from("{}\n", "utf8"));
            this.jsonUri = uri;
        }

        const target = await vscode.workspace.openTextDocument(uri);
        const edit = new vscode.WorkspaceEdit();
        edit.replace(uri, new vscode.Range(0, 0, target.lineCount, 0), dataset.jsonText(fields, rows, this.state.asArray));
        if (!(await vscode.workspace.applyEdit(edit))) {
            vscode.window.showErrorMessage("RGL : le fichier JSON n'a pas pu être modifié.");
            return;
        }
        await this.refresh();
        vscode.window.setStatusBarMessage(`RGL : ${vscode.workspace.asRelativePath(uri)} réécrit (à enregistrer).`, 4000);
    }
}

module.exports = { DatasetLensProvider, openDatasetEditor };
