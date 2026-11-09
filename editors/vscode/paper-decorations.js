// Le RGL d'une page `.paper` sur un fond un peu plus clair que le HTML qui l'entoure (comme le code d'une page Blazor), pour les distinguer d'un coup d'œil.
// La couleur est `rgl.paperRegionBackground`, réglable par le thème ou `workbench.colorCustomizations`.

const vscode = require("vscode");
const { findRegions, lineParts } = require("./paper-regions");

const isPaper = document => /\.paper$/i.test(document.fileName);

/** @param {vscode.ExtensionContext} context */
function registerPaperDecorations(context) {
    const color = new vscode.ThemeColor("rgl.paperRegionBackground");
    const partial = vscode.window.createTextEditorDecorationType({ backgroundColor: color });
    const whole = vscode.window.createTextEditorDecorationType({ backgroundColor: color, isWholeLine: true });
    context.subscriptions.push(partial, whole);

    const refresh = editor => {
        if (!editor || !isPaper(editor.document)) return;
        const text = editor.document.getText();
        const partialRanges = [];
        const wholeRanges = [];
        const range = ([from, to]) => new vscode.Range(editor.document.positionAt(from), editor.document.positionAt(to));
        for (const region of findRegions(text)) {
            const parts = lineParts(text, region.outerStart, region.outerEnd);
            partialRanges.push(...parts.partial.map(range));
            wholeRanges.push(...parts.whole.map(range));
        }
        editor.setDecorations(partial, partialRanges);
        editor.setDecorations(whole, wholeRanges);
    };

    let timer;
    const later = editor => {
        clearTimeout(timer);
        timer = setTimeout(() => refresh(editor), 80);
    };
    context.subscriptions.push(
        vscode.window.onDidChangeActiveTextEditor(editor => refresh(editor)),
        vscode.window.onDidChangeVisibleTextEditors(editors => editors.forEach(refresh)),
        vscode.workspace.onDidChangeTextDocument(event => {
            const editor = vscode.window.visibleTextEditors.find(e => e.document === event.document);
            if (editor) later(editor);
        }));
    vscode.window.visibleTextEditors.forEach(refresh);
}

module.exports = { registerPaperDecorations };
