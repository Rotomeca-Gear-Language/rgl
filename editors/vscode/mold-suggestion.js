// La valeur d'un mold en suggestion grisée : après `Vector2 vec:`, `{x: , y: }` apparaît en grisé ; Tab l'écrit et place le curseur après le `:` du premier
// champ (Tab passe au suivant). Le serveur (`rgl/moldSkeleton`) dit si le curseur est à cette place et donne l'extrait ; seuls les champs obligatoires sont écrits.

const vscode = require("vscode");

/** @param {vscode.ExtensionContext} context @param {() => import("vscode-languageclient/node").LanguageClient | undefined} getClient */
function registerMoldSuggestion(context, getClient) {
    const provider = {
        async provideInlineCompletionItems(document, position, _context, token) {
            const client = getClient();
            // Un contrôle bon marché avant d'interroger le serveur : la ligne doit finir par un deux-points.
            if (!client || !document.lineAt(position.line).text.slice(0, position.character).trimEnd().endsWith(":")) return undefined;

            let answer;
            try {
                answer = await client.sendRequest("rgl/moldSkeleton",
                    { textDocument: { uri: document.uri.toString() }, position: { line: position.line, character: position.character } }, token);
            } catch {
                return undefined;
            }
            if (!answer || typeof answer.snippet !== "string") return undefined;
            return [new vscode.InlineCompletionItem(new vscode.SnippetString(answer.snippet), new vscode.Range(position, position))];
        },
    };
    context.subscriptions.push(vscode.languages.registerInlineCompletionItemProvider({ language: "rgl" }, provider));
}

module.exports = { registerMoldSuggestion };
