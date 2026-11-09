"use strict";
const path = require("node:path");

/** Conserve le dossier d’origine pour trouver Zig/runtime après la copie privée du serveur. */
function serverOptions(compiler, original = compiler) {
    const launch = compiler.toLowerCase().endsWith(".dll")
        ? { command: "dotnet", args: [compiler, "lsp"] }
        : { command: compiler, args: ["lsp"] };
    if (original.includes("/") || original.includes("\\")) {
        launch.options = { env: { ...process.env, RGL_COMPILER_DIRECTORY: path.dirname(path.resolve(original)) } };
    }
    return launch;
}
module.exports = { serverOptions };
