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
        const scopeAt = (line, text) => {
            const index = line.indexOf(text);
            assert.notEqual(index, -1);
            return grammar.tokenizeLine(line).tokens.find(t => t.startIndex <= index && index < t.endIndex).scopes;
        };
        for (const tag of ["lang", "summary", "param", "returns", "attention", "custom-note", "acme:note"]) {
            for (const close of ["", "/"]) {
                const line = `/// <${close}${tag}>`;
                assert(scopeAt(line, tag).includes("entity.name.tag.documentation.rgl"), line);
            }
        }
        for (const line of ['/// <lang name="fr">', "/// <attention niveau = 'important'>"]) {
            const attribute = line.includes("lang") ? "name" : "niveau";
            assert(scopeAt(line, attribute).includes("entity.other.attribute-name.documentation.rgl"));
            assert(scopeAt(line, line.includes('"') ? '"fr"' : "'important'").includes("string.quoted.documentation.rgl"));
        }
        assert(!scopeAt('// <lang name="fr">', "lang").includes("entity.name.tag.documentation.rgl"));
        assert(!scopeAt('/// Le texte name="fr" reste du texte.', "name").includes("entity.other.attribute-name.documentation.rgl"));
        const snippets = JSON.parse(fs.readFileSync(path.join(__dirname, "../snippets/rgl.code-snippets"), "utf8"));
        const localized = Object.values(snippets).find(s => s.prefix === "doclang").body.join("\n");
        const blocks = [...localized.matchAll(/<lang name="[^"]+">([\s\S]*?)<\/lang>/g)];
        assert.equal(blocks.length, 2);
        for (const block of blocks) {
            for (const section of ["summary", "param", "returns"]) assert(block[1].includes(`<${section}`));
            assert(!block[1].includes("<lang"));
        }
        assert(Object.values(snippets).some(s => s.prefix === "doccustom"));
        const { onEnterRules } = require("../language-configuration.json");
        const rule = onEnterRules.find(r => new RegExp(r.beforeText).test('    /// <lang name="fr">'));
        assert.equal(rule.action.appendText, "/// ");
        assert(!new RegExp(rule.beforeText).test("//// commentaire ordinaire"));
        console.log("Documentation VS Code : coloration TextMate, snippets et continuation vérifiés.");
    } finally { registry.dispose(); }
}
main().catch(error => { console.error(error); process.exitCode = 1; });
