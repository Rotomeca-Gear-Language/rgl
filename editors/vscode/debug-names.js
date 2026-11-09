"use strict";
// Les noms que montre le débogueur : dans un cadre écrit en RGL (un fichier `.rgl`, `.forge`, `.paper`), les variables portent leur nom RGL (`n`, pas `rgl_v_n`) et
// les temporaires du C++ engendré disparaissent ; dans un cadre C++ (le runtime), les noms C++ restent. Ce fichier ne dépend pas de VS Code : `NameTracker` lit les
// messages du protocole de débogage (DAP) qui passent entre l'éditeur et le débogueur, et réécrit en place les réponses `variables` des cadres RGL.

/** Vrai pour un fichier source RGL : un cadre qui y est écrit montre des noms RGL. */
function isRglSource(file) {
    return typeof file === "string" && /\.(rgl|forge|paper)$/i.test(file);
}

/** Les types C++ que RGL écrit autrement. */
const PRIMITIVE_TYPES = new Map([
    ["int", "int"], ["unsigned int", "uint"], ["long long", "long"], ["unsigned long long", "ulong"],
    ["short", "short"], ["unsigned short", "ushort"], ["signed char", "byte"], ["unsigned char", "ubyte"],
    ["bool", "bool"], ["float", "float"], ["double", "double"],
    ["rgl::standard::primitives::string", "string"], ["rgl::standard::primitives::mstring", "mstring"], ["rgl::standard::primitives::symbol", "symbol"],
    ["std::int32_t", "int"], ["std::uint32_t", "uint"], ["std::int64_t", "long"], ["std::uint64_t", "ulong"],
    ["rgl::standard::primitives::duration", "duration"], ["rgl::standard::primitives::datetime", "datetime"],
]);

/** Les types que RGL voit comme une valeur d'un bloc : leur résumé est leur valeur, ils n'ont rien à déplier (leurs champs sont un détail du C++). */
const OPAQUE_TYPES = new Set(["string", "mstring", "symbol", "duration", "datetime", "int", "uint", "long", "ulong", "short", "ushort", "byte", "ubyte", "bool", "float", "double"]);

/** Vrai pour un type RGL (`string`, `uint?`…) dont la valeur n'a pas de contenu à montrer. */
function isOpaque(rglTypeName) {
    return typeof rglTypeName === "string" && OPAQUE_TYPES.has(rglTypeName.replace(/\?$/, ""));
}

/** Un texte de valeur (`{rgl_f_x:1, rgl_f_y:2}`) sans les préfixes C++ des champs : `{x:1, y:2}`. */
function rglValue(value) {
    return typeof value === "string" ? value.replace(/rgl_f_/g, "") : value;
}

/** Le nom RGL d'un nom C++ de mold ou d'enum : `rgl_m_Vec2` → `Vec2`, `rgl_m_std_d_Errors_d_Exception` → `std.Errors.Exception`. */
function moldName(cpp) {
    return cpp.replace(/^rgl_[mea]_/, "").replace(/_d_/g, ".");
}

/** Le type C++ d'une valeur, écrit comme RGL l'écrit. */
function rglType(type) {
    if (typeof type !== "string") return type;
    const trimmed = type.trim().replace(/^const\s+/, "").replace(/\s*&$/, "");
    if (PRIMITIVE_TYPES.has(trimmed)) return PRIMITIVE_TYPES.get(trimmed);

    const array = /^rgl::standard::array<(.+)>$/.exec(trimmed);
    if (array) return `[${rglType(array[1])}]`;
    const nullable = /^std::(?:__1::|__cxx11::)?optional<(.+)>$/.exec(trimmed);
    if (nullable) return `${rglType(nullable[1])}?`;
    const mold = /^rgl_[mea]_[A-Za-z0-9_]+$/.exec(trimmed);
    return mold ? moldName(trimmed) : type;
}

/**
 * Le nom RGL d'un nom C++ du programme engendré.
 * @returns {{ name: string, hidden: boolean } | undefined} `undefined` pour un nom qui n'est pas du programme engendré (`this`…) : il reste tel quel.
 */
function rglDisplay(cppName) {
    // Une constante de fichier est une variable globale : le débogueur la nomme `::rgl_v_nom`.
    const bare = cppName.replace(/^::/, "");
    const match = /^rgl_([vpf])_(.+)$/.exec(bare);
    if (match) return { name: match[2], hidden: false };
    // Les autres noms rgl_ (temporaires : rgl_t0, rgl_start, rgl_sw1, rgl_cp1, rgl_where…) sont des détails du C++.
    return /^rgl_/.test(bare) ? { name: bare, hidden: true } : undefined;
}

/**
 * Réécrit une liste de variables d'un cadre RGL. Deux variables du même nom RGL (une variable masquée, `rgl_v_x` et `rgl_v_x_2`) restent distinctes : la
 * suivante s'écrit `x (2)`.
 * @param {{ name: string, type?: string, variablesReference?: number }[]} variables
 * @returns {{ variables: object[], names: Map<string, string> }} La liste réécrite et, pour chaque nom affiché, le nom C++ d'origine (pour les évaluations).
 */
function translateVariables(variables, options = {}) {
    const present = new Set(variables.map(v => v.name));
    const names = new Map();
    const result = [];
    for (const variable of variables) {
        // « [raw] » : la vue C++ brute que le débogueur ajoute à une valeur qui a un formateur.
        if (variable.name === "[raw]") continue;
        const display = rglDisplay(variable.name);
        if (display?.hidden) continue;
        // Les portées Static et Global listent aussi le runtime (`::envp`…) : seules les constantes du programme RGL y sont montrées.
        if (options.onlyProgram && !display) continue;

        let shown = display?.name ?? variable.name;
        // Une variable masquée : `x_2` n'est le doublon de `x` que si `x` est là aussi.
        const shadow = display && /^(.+)_(\d+)$/.exec(shown);
        if (shadow && present.has(variable.name.replace(/_\d+$/, ""))) shown = `${shadow[1]} (${shadow[2]})`;

        if (display) names.set(shown, variable.name);
        const type = variable.type !== undefined ? rglType(variable.type) : undefined;
        const item = { ...variable, name: shown, ...(type !== undefined ? { type } : {}), ...(variable.value !== undefined ? { value: rglValue(variable.value) } : {}) };
        // Une primitive (string, symbol, durée, date, nombre, bool…) est sa valeur : pas de structure C++ à déplier.
        if (isOpaque(type)) item.variablesReference = 0;
        result.push(item);
    }
    return { variables: result, names };
}

/** Une expression que l'utilisateur écrit dans un cadre RGL, dont les noms RGL redeviennent ceux du C++ (`n + 1` → `rgl_v_n + 1`). */
function translateExpression(expression, names) {
    if (!names || names.size === 0) return expression;
    // Un mot précédé d'un point est un champ : `p.x` → `rgl_v_p.rgl_f_x`.
    return expression.replace(/(\.\s*)?([A-Za-z_][A-Za-z0-9_]*)/g, (_all, dot, word) => (dot ? `${dot}rgl_f_${word}` : names.get(word) ?? word));
}

/** Les mots du langage : les survoler n'évalue rien. */
const NOT_EVALUATED = new Set([
    "gear", "guard", "emit", "pure", "return", "const", "mut", "var", "if", "else", "switch", "case", "default", "break", "goto", "train", "done", "fail", "true", "false", "null",
    "new", "import", "use", "export", "mold", "enum", "ingot", "extend", "reactive", "as", "is", "and", "or", "not", "eq", "void",
    "string", "int", "uint", "long", "ulong", "double", "float", "bool", "byte", "ubyte", "short", "ushort", "symbol", "regex", "duration", "datetime",
]);

/**
 * L'expression que le survol d'une ligne évalue : le nom sous le curseur, avec ce qui le précède dans une chaîne `a.b.c` (survoler `x` dans `pos.x` évalue `pos.x`).
 * @param {string} line Le texte de la ligne.
 * @param {number} character La colonne du curseur.
 * @returns {{ start: number, end: number, text: string } | undefined}
 */
function expressionAt(line, character) {
    const word = /[A-Za-z_][A-Za-z0-9_]*/g;
    let found;
    for (let match = word.exec(line); match !== null; match = word.exec(line)) {
        if (match.index <= character && character <= match.index + match[0].length) {
            found = match;
            break;
        }
    }
    if (!found || NOT_EVALUATED.has(found[0])) return undefined;

    let start = found.index;
    for (;;) {
        const trimmed = line.slice(0, start).trimEnd();
        if (!trimmed.endsWith(".")) break;
        const owner = /[A-Za-z_][A-Za-z0-9_]*$/.exec(trimmed.slice(0, -1).trimEnd());
        // Un point derrière autre chose qu'un nom (`f().x`, `a[0].x`) : on ne sait pas évaluer ce qui précède.
        if (!owner) return undefined;
        start = trimmed.slice(0, -1).trimEnd().length - owner[0].length;
    }
    const end = found.index + found[0].length;
    return { start, end, text: line.slice(start, end).replace(/\s+/g, "") };
}

/**
 * Suit les messages du protocole de débogage et réécrit, en place, les variables des cadres RGL. À brancher sur un `DebugAdapterTracker` :
 * `onWillReceiveMessage` pour ce que l'éditeur envoie, `onDidSendMessage` pour ce que le débogueur répond.
 */
class NameTracker {
    /**
     * @param {{ prefetch?: (frameId: number) => void, reactives?: (path: string) => string[] }} [options]
     *   `prefetch` : appelé pour le cadre du haut de la pile quand il est écrit en RGL ; il demande ses variables au débogueur, pour que les noms soient connus
     *   avant le premier survol (le panneau Variables peut être fermé : sans lui, rien ne les demande).
     *   `reactives` : les noms des états `reactive` d'une page `.paper` ; dans la page, `count` est le champ `state.count` du C++, pas une variable.
     */
    constructor(options = {}) {
        this._prefetch = options.prefetch;
        this._reactives = options.reactives;
        this._prefetched = new Set();
        /** Les requêtes en attente de réponse, par numéro de séquence. */
        this._pending = new Map();
        /** Pour chaque cadre : vrai s'il est écrit en RGL. */
        this._frames = new Map();
        /** Pour chaque cadre : le fichier source où il est écrit. */
        this._paths = new Map();
        /** Pour chaque `variablesReference` : son cadre, s'il est RGL, et la portée (Local, Static, Global) dont il vient. */
        this._references = new Map();
        /** Pour chaque cadre RGL : nom affiché → nom C++. */
        this._names = new Map();
    }

    /** Les noms d'un cadre : ceux de ses variables, puis les états réactifs de sa page (`count` → `rgl_p_state.rgl_f_count`). */
    _namesOf(frameId) {
        const names = this._names.get(frameId);
        const path = this._paths.get(frameId);
        if (!path || !/\.paper$/i.test(path) || !this._reactives) return names;

        const merged = new Map();
        for (const reactive of this._reactives(path) ?? []) merged.set(reactive, `rgl_p_state.rgl_f_${reactive}`);
        // Une variable du cadre l'emporte sur un état du même nom.
        for (const [shown, cpp] of names ?? []) merged.set(shown, cpp);
        return merged;
    }

    /** Ce que l'éditeur envoie au débogueur : on retient les requêtes, et une évaluation reçoit les noms C++. */
    onWillReceiveMessage(message) {
        if (message?.type !== "request") return;
        this._pending.set(message.seq, message);
        if (message.command === "evaluate" && message.arguments?.frameId !== undefined && typeof message.arguments.expression === "string") {
            const names = this._namesOf(message.arguments.frameId);
            if (names) message.arguments.expression = translateExpression(message.arguments.expression, names);
        }
    }

    /** Ce que le débogueur répond : cadres, portées, variables. */
    onDidSendMessage(message) {
        if (message?.type === "event" && (message.event === "continued" || message.event === "stopped" || message.event === "terminated")) {
            // Les numéros de cadre et de référence ne valent que pour un arrêt.
            this._frames.clear();
            this._paths.clear();
            this._references.clear();
            this._names.clear();
            this._pending.clear();
            this._prefetched.clear();
            return;
        }
        if (message?.type !== "response" || !message.success) return;

        const request = this._pending.get(message.request_seq);
        this._pending.delete(message.request_seq);
        if (!request) return;

        switch (request.command) {
            case "stackTrace": {
                const frames = message.body?.stackFrames ?? [];
                for (const frame of frames) {
                    this._frames.set(frame.id, isRglSource(frame.source?.path));
                    this._paths.set(frame.id, frame.source?.path);
                }
                const top = frames[0];
                if (top && this._frames.get(top.id) === true && !this._prefetched.has(top.id)) {
                    this._prefetched.add(top.id);
                    this._prefetch?.(top.id);
                }
                break;
            }
            case "scopes": {
                const frameId = request.arguments?.frameId;
                for (const scope of message.body?.scopes ?? []) this._references.set(scope.variablesReference, { frameId, rgl: this._frames.get(frameId) === true, scope: scope.name });
                break;
            }
            case "variables":
                this._variables(request, message);
                break;
            case "evaluate": {
                // Le survol d'une valeur d'un cadre RGL : son type s'écrit comme RGL l'écrit, ses champs sans préfixe C++, une primitive sans structure à déplier.
                const frameId = request.arguments?.frameId;
                if (this._frames.get(frameId) !== true || !message.body) break;
                if (typeof message.body.type === "string") message.body.type = rglType(message.body.type);
                message.body.result = rglValue(message.body.result);
                if (isOpaque(message.body.type)) message.body.variablesReference = 0;
                if (message.body.variablesReference > 0) this._references.set(message.body.variablesReference, { frameId, rgl: true });
                break;
            }
        }
    }

    _variables(request, message) {
        const reference = this._references.get(request.arguments?.variablesReference);
        if (!reference?.rgl || !Array.isArray(message.body?.variables)) return;

        const onlyProgram = /^(static|global)/i.test(reference.scope ?? "");
        const { variables, names } = translateVariables(message.body.variables, { onlyProgram });
        // Réécrit en place : c'est le même objet que l'éditeur recevra.
        message.body.variables.splice(0, message.body.variables.length, ...variables);

        // Les premières portées l'emportent : une variable locale masque une constante de fichier de même nom.
        const known = this._names.get(reference.frameId) ?? new Map();
        for (const [shown, cpp] of names) if (!known.has(shown)) known.set(shown, cpp);
        this._names.set(reference.frameId, known);
        for (const variable of variables) {
            if (variable.variablesReference > 0) this._references.set(variable.variablesReference, { frameId: reference.frameId, rgl: true });
        }
    }
}

module.exports = { isRglSource, rglDisplay, rglType, moldName, translateVariables, translateExpression, expressionAt, isOpaque, rglValue, NOT_EVALUATED, NameTracker };
