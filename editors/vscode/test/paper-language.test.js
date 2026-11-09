"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { Registry, parseRawGrammar } = require("vscode-textmate");
const { loadWASM, OnigScanner, OnigString } = require("vscode-oniguruma");

async function main() {
    const root = path.join(__dirname, "..");
    const extension = JSON.parse(fs.readFileSync(path.join(root, "package.json"), "utf8"));
    const html = extension.contributes.languages.find(language => language.id === "html");
    assert(html.extensions.includes(".paper"), ".paper doit être associé au langage HTML");
    const paper = extension.contributes.grammars.find(grammar => grammar.scopeName === "source.rgl.embedded.paper");
    // VS Code tokenise un fichier HTML avec la racine text.html.derivative : l'injection doit la viser, sinon aucune coloration n'est appliquée.
    assert(paper.injectTo.includes("text.html.derivative"));
    assert(paper.injectTo.includes("text.html.basic"));

    await loadWASM(fs.readFileSync(require.resolve("vscode-oniguruma/release/onig.wasm")));
    const htmlGrammar = {
        scopeName: "text.html.basic",
        patterns: [{ match: "</?([A-Za-z][A-Za-z0-9-]*)", captures: { 1: { name: "entity.name.tag.html" } } }],
    };
    const files = {
        "text.html.basic": JSON.stringify(htmlGrammar),
        "text.html.derivative": JSON.stringify({ scopeName: "text.html.derivative", patterns: [{ include: "text.html.basic" }] }),
        "source.rgl": fs.readFileSync(path.join(root, "syntaxes/rgl.tmLanguage.json"), "utf8"),
        "source.rgl.embedded.paper": fs.readFileSync(path.join(root, "syntaxes/paper.tmLanguage.json"), "utf8"),
    };
    const registry = new Registry({
        onigLib: Promise.resolve({ createOnigScanner: patterns => new OnigScanner(patterns), createOnigString: value => new OnigString(value) }),
        loadGrammar: async scope => files[scope] ? parseRawGrammar(files[scope], `${scope}.json`) : null,
        getInjections: scope => paper.injectTo.includes(scope) ? ["source.rgl.embedded.paper"] : [],
    });
    try {
        const grammar = await registry.loadGrammar("text.html.derivative");
        const scopesAt = (result, line, text) => {
            const index = line.indexOf(text);
            assert(index >= 0);
            return result.tokens.find(token => token.startIndex <= index && index < token.endIndex).scopes;
        };

        const htmlLine = "<main>Bonjour</main>";
        assert(scopesAt(grammar.tokenizeLine(htmlLine), htmlLine, "main").includes("entity.name.tag.html"));

        let state = null;
        const lines = [
            "@rgl {",
            "  gear label(int value): string {",
            "    return value > 0 ? 'oui' : 'non';",
            "  }",
            "}",
            "<p>@@ html(label(1)) @@</p>",
        ];
        for (const [index, line] of lines.entries()) {
            const result = grammar.tokenizeLine(line, state);
            state = result.ruleStack;
            if (index === 1) assert(scopesAt(result, line, "gear").includes("storage.type.function.rgl"));
            if (index === 2) assert(scopesAt(result, line, "return").includes("keyword.control.rgl"));
            if (index === 5) {
                assert(scopesAt(result, line, "html").includes("meta.embedded.expression.rgl.paper"));
                assert(scopesAt(result, line, "p").includes("entity.name.tag.html"));
                // Le @@ fermant ferme l'interpolation : le HTML qui suit garde ses portées (l'injection ne s'applique pas dans ses propres zones).
                const closing = line.indexOf("</p>") + 2;
                const closingScopes = result.tokens.find(token => token.startIndex <= closing && closing < token.endIndex).scopes;
                assert(closingScopes.includes("entity.name.tag.html"), "la balise fermante après @@ … @@ doit rester du HTML");
                assert(!closingScopes.includes("meta.embedded.expression.rgl.paper"), "l'interpolation doit être fermée");
            }
        }
        console.log("VS Code Paper : HTML natif et régions RGL colorés.");
    } finally {
        registry.dispose();
    }
}

main().catch(error => { console.error(error); process.exitCode = 1; });
