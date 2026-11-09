"use strict";
// Les tests RGL dans l'explorateur de tests. Un test est un fichier `nom.test.rgl` : il se lance seul (`rgl exec nom.test.rgl`) et réussit s'il rend
// le code 0. Les vérifications de la std (`Test`) écrivent « ÉCHEC (fichier:ligne:colonne) : raison » sur la sortie d'erreur et arrêtent le programme
// avec le code 1 ; ce fichier lit ce rapport. Il ne dépend pas de VS Code.

const path = require("path");
const { commandLine } = require("./tasks");

const TEST_SUFFIX = ".test.rgl";

/** Vrai pour un fichier de test. */
function isTestFile(file) {
    return file.toLowerCase().endsWith(TEST_SUFFIX);
}

/** Le nom d'un test : le fichier sans son suffixe. */
function testName(file) {
    return path.basename(file).slice(0, -TEST_SUFFIX.length);
}

/** La ligne de commande qui lance un fichier de test en debug. */
function runCommand(file, compiler) {
    return commandLine({ command: "exec", file }, compiler);
}

/**
 * Ce qu'une exécution dit de son échec : le dernier rapport « ÉCHEC (…) » de la sortie, sinon la première erreur de compilation (`error[E003] : … --> chemin:ligne:colonne`).
 * @param {string} output La sortie d'erreur et la sortie standard.
 * @returns {{ message: string, file?: string, line?: number, column?: number }}
 */
function readFailure(output) {
    const text = output.replace(/\x1b\[[0-9;]*m/g, "");
    const lines = text.split(/\r?\n/);

    for (let i = lines.length - 1; i >= 0; i--) {
        const found = /^ÉCHEC \((.+):(\d+):(\d+)\) : (.*)$/.exec(lines[i]);
        if (found) return { message: found[4], file: found[1], line: Number(found[2]), column: Number(found[3]) };
    }

    for (let i = 0; i < lines.length; i++) {
        const header = /^(?:error|erreur)(?:\[(\w+)\])?\s*:\s*(.*)$/i.exec(lines[i]);
        if (!header) continue;
        const where = /^\s*-->\s*(.+):(\d+):(\d+)\s*$/.exec(lines[i + 1] ?? "");
        const code = header[1] ? `[${header[1]}] ` : "";
        return where
            ? { message: code + header[2], file: where[1], line: Number(where[2]), column: Number(where[3]) }
            : { message: code + header[2] };
    }

    const tail = lines.filter(l => l.trim().length > 0).slice(-5).join("\n");
    return { message: tail.length > 0 ? tail : "le test a échoué sans rien écrire" };
}

module.exports = { TEST_SUFFIX, isTestFile, testName, runCommand, readFailure };
