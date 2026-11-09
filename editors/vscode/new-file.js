"use strict";
// « Nouveau fichier RGL » : le clic droit de l'explorateur propose un fichier `.rgl` (le point d'entrée du projet) ou `.forge` (un fichier source) ; pour un
// `.forge`, le genre et la description s'écrivent dans l'en-tête. Ce fichier ne dépend pas de VS Code : il valide le nom, décrit les genres et écrit le
// contenu ; new-file-command.js pose les questions et crée le fichier.

/** Ce que l'on peut créer. */
const FILE_KINDS = [
    { extension: ".rgl", label: "Fichier RGL (.rgl)", description: "Le point d'entrée du projet : il contient le train d'entrée" },
    { extension: ".forge", label: "Fichier forge (.forge)", description: "Un fichier source, importé par son nom" },
];

/** Les genres d'un fichier forge : ce qu'ils contiennent, pour choisir. */
const FORGE_KINDS = [
    { kind: "gears", label: "gears", description: "Gears, guards, emits, constantes et extensions" },
    { kind: "mold", label: "mold", description: "Molds, enums, ingots et imports" },
    { kind: "train", label: "train", description: "Un train appelable, avec son train par défaut" },
    { kind: "extend", label: "extend", description: "Extensions appliquées à tout le programme, sans import" },
    { kind: "precast", label: "precast", description: "Données constantes (precasts)" },
    { kind: "descriptor", label: "descriptor", description: "Une classe C++ vue de RGL" },
    { kind: "native", label: "native", description: "Une bibliothèque C (en-têtes et liaison)" },
    { kind: "index", label: "index", description: "L'index d'une box (index.forge, dans un dossier Nom.box)" },
];

/** Les caractères d'un nom de fichier : le nom d'un `.forge` est celui qu'un import écrit. */
const NAME_PATTERN = /^[A-Za-z_][A-Za-z0-9_-]*$/;

/**
 * Vérifie le nom saisi (sans extension). Rend `undefined` s'il convient, sinon le message et sa gravité.
 * @param {string} name
 * @param {".rgl" | ".forge"} extension
 * @param {{ existing: string[] }} folder Les noms de fichiers déjà dans le dossier.
 * @returns {{ message: string, severity: "error" | "warning" } | undefined}
 */
function validateName(name, extension, folder) {
    const trimmed = name.trim();
    if (trimmed.length === 0) return { message: "Écrivez un nom de fichier.", severity: "error" };
    if (/\.(rgl|forge)$/i.test(trimmed)) return { message: "Écrivez le nom sans l'extension : elle est choisie par la liste précédente.", severity: "error" };
    // Un .rgl peut être un test : « nom.test.rgl », lancé seul par l'explorateur de tests.
    const stem = extension === ".rgl" ? trimmed.replace(/\.test$/i, "") : trimmed;
    if (!NAME_PATTERN.test(stem)) {
        return { message: "Le nom commence par une lettre ou « _ » et ne contient que des lettres, chiffres, « _ » et « - » (un import l'écrit tel quel).", severity: "error" };
    }

    const file = trimmed + extension;
    if (folder.existing.some(existing => existing.toLowerCase() === file.toLowerCase())) return { message: `${file} existe déjà dans ce dossier.`, severity: "error" };

    // Un projet n'a qu'un fichier .rgl (hors fichiers de test) : avertir sans empêcher.
    if (extension === ".rgl" && !/\.test$/i.test(trimmed) && folder.existing.some(existing => /\.rgl$/i.test(existing) && !/\.test\.rgl$/i.test(existing))) {
        return { message: "Ce dossier a déjà un fichier .rgl : un projet n'en a qu'un (sauf les fichiers nom.test.rgl).", severity: "warning" };
    }
    return undefined;
}

/** La description écrite comme une chaîne RGL : entre apostrophes, sinon entre guillemets quand elle contient une apostrophe. */
function quoteDescription(description) {
    const text = description.replace(/\s+/g, " ").trim();
    if (!text.includes("'")) return `'${text}'`;
    return `"${text.replace(/\\/g, "\\\\").replace(/"/g, '\\"')}"`;
}

/**
 * Le contenu du nouveau fichier.
 * @param {".rgl" | ".forge"} extension
 * @param {{ forgeKind?: string, description?: string }} [options]
 */
function fileContent(extension, options = {}) {
    if (extension === ".rgl") return "train |> done;\n";

    const kind = options.forgeKind ?? "gears";
    const header = `forge: ${kind}\ndescription: ${quoteDescription(options.description ?? "")}\n`;
    // Un fichier de train a besoin de son train par défaut, écrit en dernier : sans lui, l'erreur est là dès la création.
    return kind === "train" ? `${header}\ntrain |> done;\n` : `${header}\n`;
}

/** Les dossiers spéciaux : une box (`Nom.box`, opaque, avec son index) et un dossier d'outils (`Nom.tools`, dont tous les fichiers s'importent d'un coup). */
const FOLDER_KINDS = {
    box: { suffix: ".box", label: "Box", prompt: "Nom de la box, sans « .box » (importée par `import box Nom;`)" },
    tools: { suffix: ".tools", label: "Tools", prompt: "Nom du dossier d'outils, sans « .tools » (importé par `import tools Nom;`)" },
};

/**
 * Vérifie le nom d'un dossier `Nom.box` ou `Nom.tools` (saisi sans le suffixe).
 * @param {string} name
 * @param {".box" | ".tools"} suffix
 * @param {{ existing: string[] }} folder Les noms déjà dans le dossier.
 * @returns {{ message: string, severity: "error" | "warning" } | undefined}
 */
function validateFolderName(name, suffix, folder) {
    const trimmed = name.trim();
    if (trimmed.length === 0) return { message: "Écrivez un nom de dossier.", severity: "error" };
    if (trimmed.toLowerCase().endsWith(suffix)) return { message: `Écrivez le nom sans « ${suffix} » : le suffixe est ajouté.`, severity: "error" };
    if (!NAME_PATTERN.test(trimmed)) {
        return { message: "Le nom commence par une lettre ou « _ » et ne contient que des lettres, chiffres, « _ » et « - » (un import l'écrit tel quel).", severity: "error" };
    }

    const folderName = trimmed + suffix;
    if (folder.existing.some(existing => existing.toLowerCase() === folderName.toLowerCase())) return { message: `${folderName} existe déjà dans ce dossier.`, severity: "error" };
    return undefined;
}

/** Le `index.forge` d'une nouvelle box : l'index n'a que des imports et un `export {…};`, expliqués en commentaires. */
function indexContent(description = "") {
    return `forge: index\ndescription: ${quoteDescription(description)}\n\n// Importez les fichiers de la box, puis exportez leurs noms :\n// import Fichier;\n// export {...Fichier};\n`;
}

module.exports = { FILE_KINDS, FORGE_KINDS, FOLDER_KINDS, NAME_PATTERN, validateName, validateFolderName, quoteDescription, fileContent, indexContent };
