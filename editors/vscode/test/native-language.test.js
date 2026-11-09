"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { Registry, parseRawGrammar } = require("vscode-textmate");
const { loadWASM, OnigScanner, OnigString } = require("vscode-oniguruma");

async function main() {
    await loadWASM(fs.readFileSync(require.resolve("vscode-oniguruma/release/onig.wasm")));
    const registry = new Registry({
        onigLib: Promise.resolve({ createOnigScanner: p => new OnigScanner(p), createOnigString: s => new OnigString(s) }),
        loadGrammar: async scope => scope !== "source.rgl" ? null : parseRawGrammar(fs.readFileSync(path.join(__dirname, "../syntaxes/rgl.tmLanguage.json"), "utf8"), "rgl.json"),
    });
    try {
        const grammar = await registry.loadGrammar("source.rgl");
        function scopes(line, word) {
            const index = line.indexOf(word);
            assert(index >= 0);
            return grammar.tokenizeLine(line).tokens.find(t => t.startIndex <= index && index < t.endIndex).scopes;
        }
        for (const declaration of ["unsafe lire(uptr<int> p): int;", "unsafe lire<T>(uptr<T> p): T;"]) {
            assert(scopes(declaration, "unsafe").includes("storage.type.function.rgl"));
            assert(scopes(declaration, "lire").includes("entity.name.function.rgl"));
        }
        for (const kind of ["train", "mold", "gears", "extend", "index", "native", "precast", "descriptor"]) {
            assert(scopes(`forge: ${kind}`, kind).includes("support.constant.forge-kind.rgl"));
        }
        for (const type of ["ptr", "uptr", "char"]) {
            assert(scopes(`${type}<int> p: 5;`, type).includes("storage.type.primitive.rgl"));
        }
        for (const key of ["header", "link"]) {
            assert(scopes(`${key}: "api.h"`, key).includes("keyword.other.header.rgl"));
        }
        let state = null;
        const lines = [
            'return cpp {',
            '#include <cmath>',
            'double rgl(double x) {',
            '    const char* text = R"tag( } )tag";',
            '    return std::sqrt(x);',
            '}',
            '}(double x: x): double;',
            'int resultat: 2;',
        ];
        for (let i = 0; i < lines.length; i++) {
            const result = grammar.tokenizeLine(lines[i], state);
            state = result.ruleStack;
            if (i > 0 && i < 6) assert(result.tokens.some(t => t.scopes.includes("meta.embedded.block.cpp")), lines[i]);
            if (i === 7) assert(result.tokens.every(t => !t.scopes.includes("meta.embedded.block.cpp")), "Le RGL après le bloc reste du RGL");
        }
        console.log("VS Code natif : coloration unsafe, pointeurs et en-têtes A2 vérifiée.");
    } finally { registry.dispose(); }
}
main().catch(error => { console.error(error); process.exitCode = 1; });
