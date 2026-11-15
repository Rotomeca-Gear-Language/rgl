"use strict";
// Les paquets RGL côté éditeur : ce qui se vérifie sans VS Code (adresse saisie, dépendances d'un manifeste). Les commandes elles-mêmes sont dans
// package-commands.js, qui lance `rgl add`, `rgl restore`… comme des tâches.

const HOSTS = ["github.com", "gitlab.com", "codeberg.org"];
const SEGMENT = /^[A-Za-z0-9._-]+$/;
const VERSION = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/;

/**
 * Ce que l'utilisateur a saisi pour `rgl add` : `github.com/alice/json` ou `github.com/alice/json@1.2.0`.
 * @param {string} text
 * @returns {string | null} Pourquoi c'est invalide, ou `null` s'il convient.
 */
function validateAddress(text) {
    const value = text.trim();
    if (value.length === 0) return "écris l'adresse du dépôt du paquet, par exemple github.com/alice/json";
    if (/^[a-z]+:\/\//i.test(value)) return "l'adresse s'écrit sans https:// : github.com/alice/json";

    const at = value.lastIndexOf("@");
    const address = at < 0 ? value : value.slice(0, at);
    const version = at < 0 ? null : value.slice(at + 1);
    const parts = address.split("/");
    if (parts.length !== 3) return "une adresse est hôte/compte/dépôt, par exemple github.com/alice/json";
    if (!HOSTS.includes(parts[0].toLowerCase())) return `l'hôte doit être ${HOSTS.join(", ")}`;
    if (parts.slice(1).some(part => !SEGMENT.test(part))) return "le compte et le dépôt s'écrivent avec des lettres, des chiffres, « . », « _ » et « - »";
    if (parts[2].toLowerCase().endsWith(".git")) return "écris le nom du dépôt sans « .git »";
    if (version !== null && !VERSION.test(version)) return "la version s'écrit exactement, avec trois nombres (1.2.0), sans intervalle ni préfixe";
    return null;
}

/**
 * Les dépendances d'un `rgl.json`, triées par adresse.
 * @param {string} text Le contenu du manifeste.
 * @returns {{ address: string, version: string }[]} Vide si le manifeste ne se lit pas ou n'a pas de dépendances.
 */
function dependenciesOf(text) {
    let manifest;
    try {
        manifest = JSON.parse(text);
    } catch {
        return [];
    }
    const dependencies = manifest && typeof manifest === "object" ? manifest.dependencies : undefined;
    if (!dependencies || typeof dependencies !== "object" || Array.isArray(dependencies)) return [];
    return Object.entries(dependencies)
        .filter(([, version]) => typeof version === "string")
        .map(([address, version]) => ({ address, version }))
        .sort((a, b) => (a.address < b.address ? -1 : a.address > b.address ? 1 : 0));
}

module.exports = { HOSTS, validateAddress, dependenciesOf };
