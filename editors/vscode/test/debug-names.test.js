"use strict";
const assert = require("node:assert/strict");
const { isRglSource, rglDisplay, rglType, moldName, translateVariables, translateExpression, expressionAt, NameTracker } = require("../debug-names");

// Quels fichiers sont du RGL.
assert.equal(isRglSource("C:\\p\\main.rgl"), true);
assert.equal(isRglSource("/p/Commandes.FORGE"), true);
assert.equal(isRglSource("/p/Home.paper"), true);
assert.equal(isRglSource("C:\\runtime\\string.hpp"), false);
assert.equal(isRglSource(undefined), false);

// Les noms.
assert.deepEqual(rglDisplay("rgl_v_total"), { name: "total", hidden: false });
assert.deepEqual(rglDisplay("rgl_p_texte"), { name: "texte", hidden: false });
assert.deepEqual(rglDisplay("rgl_f_x"), { name: "x", hidden: false });
assert.equal(rglDisplay("rgl_t0").hidden, true);
assert.equal(rglDisplay("rgl_start").hidden, true);
assert.equal(rglDisplay("rgl_cp1_k0").hidden, true);
assert.equal(rglDisplay("this"), undefined);

// Les types.
assert.equal(rglType("unsigned int"), "uint");
assert.equal(rglType("const unsigned int"), "uint");
assert.equal(rglType("rgl::standard::primitives::string"), "string");
assert.equal(rglType("rgl::standard::primitives::symbol"), "symbol");
assert.equal(rglType("rgl::standard::array<int>"), "[int]");
assert.equal(rglType("rgl_m_Vec2"), "Vec2");
assert.equal(rglType("rgl_m_std_d_Errors_d_Exception"), "std.Errors.Exception");
assert.equal(rglType("std::vector<int>"), "std::vector<int>");
assert.equal(moldName("rgl_e_Direction"), "Direction");

// Une liste de variables : renommées, sans temporaires, une variable masquée distinguée.
const { variables, names } = translateVariables([
    { name: "rgl_p_texte", value: "{…}", type: "rgl::standard::primitives::string", variablesReference: 11 },
    { name: "rgl_v_n", value: "7", type: "unsigned int" },
    { name: "rgl_t0", value: "1", type: "int" },
    { name: "rgl_v_x", value: "1", type: "int" },
    { name: "rgl_v_x_2", value: "2", type: "int" },
    { name: "rgl_v_a_1", value: "3", type: "int" },
    { name: "this", value: "0x1" },
]);
assert.deepEqual(variables.map(v => v.name), ["texte", "n", "x", "x (2)", "a_1", "this"]);
assert.deepEqual(variables.map(v => v.type), ["string", "uint", "int", "int", "int", undefined]);
// Une primitive (string…) est sa valeur : rien à déplier.
assert.equal(variables[0].variablesReference, 0);
assert.equal(names.get("n"), "rgl_v_n");
assert.equal(names.get("x (2)"), "rgl_v_x_2");

// Les expressions retrouvent les noms C++.
assert.equal(translateExpression("n + total * 2", new Map([["n", "rgl_v_n"], ["total", "rgl_v_total"]])), "rgl_v_n + rgl_v_total * 2");
assert.equal(translateExpression("autre", new Map([["n", "rgl_v_n"]])), "autre");

// Une conversation avec le débogueur.
const tracker = new NameTracker();
const toAdapter = (seq, command, args) => tracker.onWillReceiveMessage({ type: "request", seq, command, arguments: args });
const fromAdapter = (requestSeq, body) => {
    const message = { type: "response", request_seq: requestSeq, success: true, body };
    tracker.onDidSendMessage(message);
    return message;
};

toAdapter(1, "stackTrace", { threadId: 1 });
fromAdapter(1, { stackFrames: [{ id: 1, source: { path: "C:\\p\\main.rgl" } }, { id: 2, source: { path: "C:\\rgl\\runtime\\string.hpp" } }] });

// Le cadre RGL : noms RGL, et les enfants d'une valeur aussi.
toAdapter(2, "scopes", { frameId: 1 });
fromAdapter(2, { scopes: [{ name: "Locals", variablesReference: 10 }] });
toAdapter(3, "variables", { variablesReference: 10 });
const locals = fromAdapter(3, { variables: [
    { name: "rgl_p_v", value: "{rgl_f_x:1}", type: "rgl_m_Vec2", variablesReference: 11 },
    { name: "rgl_v_n", value: "7", type: "unsigned int", variablesReference: 0 },
    { name: "rgl_t1", value: "0", type: "int", variablesReference: 0 },
] });
assert.deepEqual(locals.body.variables.map(v => v.name), ["v", "n"]);
assert.equal(locals.body.variables[0].value, "{x:1}");
assert.equal(locals.body.variables[0].type, "Vec2");
assert.equal(locals.body.variables[1].type, "uint");

toAdapter(4, "variables", { variablesReference: 11 });
const children = fromAdapter(4, { variables: [{ name: "rgl_f_x", value: "1" }, { name: "size_", value: "7" }, { name: "rgl_life", value: "{}" }] });
assert.deepEqual(children.body.variables.map(v => v.name), ["x", "size_"]);

// Une évaluation dans le cadre RGL reçoit les noms C++.
const evaluate = { type: "request", seq: 5, command: "evaluate", arguments: { frameId: 1, expression: "n + 1" } };
tracker.onWillReceiveMessage(evaluate);
assert.equal(evaluate.arguments.expression, "rgl_v_n + 1");

// Le cadre C++ : rien ne change.
toAdapter(6, "scopes", { frameId: 2 });
fromAdapter(6, { scopes: [{ name: "Locals", variablesReference: 20 }] });
toAdapter(7, "variables", { variablesReference: 20 });
const cpp = fromAdapter(7, { variables: [{ name: "this", value: "0x1" }, { name: "rgl_v_n", value: "7" }, { name: "rgl_t0", value: "1" }] });
assert.deepEqual(cpp.body.variables.map(v => v.name), ["this", "rgl_v_n", "rgl_t0"]);
const cppEvaluate = { type: "request", seq: 8, command: "evaluate", arguments: { frameId: 2, expression: "n" } };
tracker.onWillReceiveMessage(cppEvaluate);
assert.equal(cppEvaluate.arguments.expression, "n");

// Un nouvel arrêt oublie les cadres : une portée inconnue n'est pas réécrite.
tracker.onDidSendMessage({ type: "event", event: "stopped" });
toAdapter(9, "variables", { variablesReference: 10 });
const stale = fromAdapter(9, { variables: [{ name: "rgl_v_n", value: "7" }] });
assert.deepEqual(stale.body.variables.map(v => v.name), ["rgl_v_n"]);

// Des messages inattendus ne cassent rien.
tracker.onWillReceiveMessage(undefined);
tracker.onDidSendMessage({ type: "response", request_seq: 99, success: false });
tracker.onDidSendMessage({ type: "response", request_seq: 100, success: true });

// Un champ : `p.x` s'évalue `rgl_v_p.rgl_f_x`.
assert.equal(translateExpression("p.x + n", new Map([["p", "rgl_v_p"], ["n", "rgl_v_n"]])), "rgl_v_p.rgl_f_x + rgl_v_n");
assert.equal(translateExpression("a . b", new Map([["a", "rgl_v_a"]])), "rgl_v_a . rgl_f_b");

// L'expression que le survol évalue : le nom sous le curseur, avec sa chaîne `a.b.c`.
const at = (line, col) => expressionAt(line, col);
assert.deepEqual(at("    const uint total: n + 1;", 22), { start: 22, end: 23, text: "n" });
assert.deepEqual(at("    return total;", 12), { start: 11, end: 16, text: "total" });
assert.deepEqual(at("    return total;", 16), { start: 11, end: 16, text: "total" }); // le curseur juste après le mot
assert.equal(at("    pos.x <- 3;", 8).text, "pos.x");
assert.equal(at("    pos.x <- 3;", 5).text, "pos");
assert.equal(at("    a.b.c <- 3;", 8).text, "a.b.c");
assert.equal(at("    a . b <- 3;", 8).text, "a.b");
// Ce qu'on ne sait pas évaluer : un mot du langage, un nom précédé d'autre chose qu'un nom, du blanc.
assert.equal(at("    return total;", 6), undefined);
assert.equal(at("    gear f(): int {", 6), undefined);
assert.equal(at("    f().x <- 1;", 8), undefined);
assert.equal(at("    x <- 12;", 11), undefined);
assert.equal(at("", 0), undefined);

// Le préchargement : le cadre RGL du haut de la pile, une fois ; jamais un cadre C++.
const asked = [];
const prefetching = new NameTracker({ prefetch: frameId => asked.push(frameId) });
prefetching.onWillReceiveMessage({ type: "request", seq: 1, command: "stackTrace", arguments: {} });
prefetching.onDidSendMessage({ type: "response", request_seq: 1, success: true, body: { stackFrames: [{ id: 7, source: { path: "C:\\p\\main.rgl" } }, { id: 8, source: { path: "C:\\p\\Home.paper" } }] } });
prefetching.onWillReceiveMessage({ type: "request", seq: 2, command: "stackTrace", arguments: {} });
prefetching.onDidSendMessage({ type: "response", request_seq: 2, success: true, body: { stackFrames: [{ id: 7, source: { path: "C:\\p\\main.rgl" } }] } });
assert.deepEqual(asked, [7]);
prefetching.onDidSendMessage({ type: "event", event: "stopped" });
prefetching.onWillReceiveMessage({ type: "request", seq: 3, command: "stackTrace", arguments: {} });
prefetching.onDidSendMessage({ type: "response", request_seq: 3, success: true, body: { stackFrames: [{ id: 9, source: { path: "C:\\rt\\string.hpp" } }] } });
assert.deepEqual(asked, [7]);

// Un survol : le type d'une valeur d'un cadre RGL s'écrit en RGL et ses éléments sont des variables RGL ; un cadre C++ garde son type.
const hovering = new NameTracker();
hovering.onWillReceiveMessage({ type: "request", seq: 1, command: "stackTrace", arguments: {} });
hovering.onDidSendMessage({ type: "response", request_seq: 1, success: true, body: { stackFrames: [{ id: 1, source: { path: "C:\\p\\main.rgl" } }, { id: 2, source: { path: "C:\\rt\\array.hpp" } }] } });
const evaluate1 = { type: "request", seq: 2, command: "evaluate", arguments: { frameId: 1, expression: "liste" } };
hovering.onWillReceiveMessage(evaluate1);
const hovered = { type: "response", request_seq: 2, success: true, body: { result: "[3 éléments]", type: "rgl::standard::array<int>", variablesReference: 40 } };
hovering.onDidSendMessage(hovered);
assert.equal(hovered.body.type, "[int]");
hovering.onWillReceiveMessage({ type: "request", seq: 3, command: "variables", arguments: { variablesReference: 40 } });
const elements = { type: "response", request_seq: 3, success: true, body: { variables: [{ name: "[0]", value: "1" }, { name: "rgl_t0", value: "9" }] } };
hovering.onDidSendMessage(elements);
assert.deepEqual(elements.body.variables.map(v => v.name), ["[0]"]);
hovering.onWillReceiveMessage({ type: "request", seq: 4, command: "evaluate", arguments: { frameId: 2, expression: "x" } });
const hoveredCpp = { type: "response", request_seq: 4, success: true, body: { result: "1", type: "unsigned int" } };
hovering.onDidSendMessage(hoveredCpp);
assert.equal(hoveredCpp.body.type, "unsigned int");

// Les primitives sans structure : string, symbol, durée, date, optionnel de primitive ; le « [raw] » du débogueur disparaît ; un mold garde ses champs.
const { isOpaque, rglValue } = require("../debug-names");
const prim = translateVariables([
    { name: "rgl_v_s", value: '"x"', type: "rgl::standard::primitives::string", variablesReference: 5 },
    { name: "rgl_v_d", value: "1500ms", type: "rgl::standard::primitives::duration", variablesReference: 6 },
    { name: "rgl_v_o", value: '"ici"', type: "std::__1::optional<rgl::standard::primitives::string>", variablesReference: 7 },
    { name: "rgl_v_p", value: '{rgl_f_x:1, rgl_f_y:2}', type: "rgl_m_Point", variablesReference: 8 },
    { name: "rgl_v_l", value: "[3 éléments]", type: "rgl::standard::array<int>", variablesReference: 9 },
    { name: "[raw]", value: "rgl::standard::array<int>", variablesReference: 10 },
]).variables;
assert.deepEqual(prim.map(v => v.name), ["s", "d", "o", "p", "l"]);
assert.deepEqual(prim.map(v => v.type), ["string", "duration", "string?", "Point", "[int]"]);
assert.deepEqual(prim.map(v => v.variablesReference), [0, 0, 0, 8, 9]);
assert.equal(prim[3].value, "{x:1, y:2}");
assert.equal(isOpaque("uint?"), true);
assert.equal(isOpaque("Point"), false);
assert.equal(isOpaque("[int]"), false);
assert.equal(rglValue(7), 7);
assert.equal(rglType("std::__cxx11::optional<int>"), "int?");

// Une constante de fichier est une globale `::rgl_v_nom` ; les portées Static et Global ne montrent que celles du programme.
assert.deepEqual(rglDisplay("::rgl_v_title"), { name: "title", hidden: false });
assert.equal(rglDisplay("::envp"), undefined);
const globals = translateVariables([{ name: "::envp", value: "0x0" }, { name: "::rgl_v_title", value: '"RGL"', type: "rgl::standard::primitives::string" }, { name: "::rgl_l_3", value: "x" }], { onlyProgram: true });
assert.deepEqual(globals.variables.map(v => v.name), ["title"]);
assert.equal(globals.names.get("title"), "::rgl_v_title");

// Un cadre dont les portées sont toutes lues : le local l'emporte sur la constante de fichier de même nom ; la constante se survole par son nom.
const scoped = new NameTracker();
const ask = (seq, command, args) => scoped.onWillReceiveMessage({ type: "request", seq, command, arguments: args });
const answer = (requestSeq, body) => { const m = { type: "response", request_seq: requestSeq, success: true, body }; scoped.onDidSendMessage(m); return m; };
ask(1, "stackTrace", {});
answer(1, { stackFrames: [{ id: 3, source: { path: "C:\p\main.rgl" } }] });
ask(2, "scopes", { frameId: 3 });
answer(2, { scopes: [{ name: "Local", variablesReference: 30 }, { name: "Global", variablesReference: 31 }] });
ask(3, "variables", { variablesReference: 30 });
answer(3, { variables: [{ name: "rgl_v_n", value: "1", type: "int" }, { name: "rgl_v_dup", value: "1", type: "int" }] });
ask(4, "variables", { variablesReference: 31 });
const glob = answer(4, { variables: [{ name: "::envp", value: "0" }, { name: "::rgl_v_title", value: '"T"', type: "rgl::standard::primitives::string" }, { name: "::rgl_v_dup", value: "9", type: "int" }] });
assert.deepEqual(glob.body.variables.map(v => v.name), ["title", "dup"]);
const hoverTitle = { type: "request", seq: 5, command: "evaluate", arguments: { frameId: 3, expression: "title + dup" } };
scoped.onWillReceiveMessage(hoverTitle);
assert.equal(hoverTitle.arguments.expression, "::rgl_v_title + rgl_v_dup");

// Une page : `count` est le champ `state.count` du C++, pas une variable ; une variable du cadre l'emporte.
const page = new NameTracker({ reactives: path => (path.endsWith("Home.paper") ? ["count", "n"] : []) });
page.onWillReceiveMessage({ type: "request", seq: 1, command: "stackTrace", arguments: {} });
page.onDidSendMessage({ type: "response", request_seq: 1, success: true, body: { stackFrames: [{ id: 4, source: { path: "C:\p\src\Home.paper" } }] } });
const hoverCount = { type: "request", seq: 2, command: "evaluate", arguments: { frameId: 4, expression: "count + 1" } };
page.onWillReceiveMessage(hoverCount);
assert.equal(hoverCount.arguments.expression, "rgl_p_state.rgl_f_count + 1");
page.onWillReceiveMessage({ type: "request", seq: 3, command: "scopes", arguments: { frameId: 4 } });
page.onDidSendMessage({ type: "response", request_seq: 3, success: true, body: { scopes: [{ name: "Local", variablesReference: 40 }] } });
page.onWillReceiveMessage({ type: "request", seq: 4, command: "variables", arguments: { variablesReference: 40 } });
page.onDidSendMessage({ type: "response", request_seq: 4, success: true, body: { variables: [{ name: "rgl_v_n", value: "1", type: "int" }] } });
const hoverShadow = { type: "request", seq: 5, command: "evaluate", arguments: { frameId: 4, expression: "n + count" } };
page.onWillReceiveMessage(hoverShadow);
assert.equal(hoverShadow.arguments.expression, "rgl_v_n + rgl_p_state.rgl_f_count");

// Un survol : le résultat d'une primitive n'a plus de structure ; un mold montre ses champs sans préfixe.
const hoverResult = new NameTracker();
hoverResult.onWillReceiveMessage({ type: "request", seq: 1, command: "stackTrace", arguments: {} });
hoverResult.onDidSendMessage({ type: "response", request_seq: 1, success: true, body: { stackFrames: [{ id: 6, source: { path: "C:\p\main.rgl" } }] } });
hoverResult.onWillReceiveMessage({ type: "request", seq: 2, command: "evaluate", arguments: { frameId: 6, expression: "x" } });
const evalString = { type: "response", request_seq: 2, success: true, body: { result: '"bonjour"', type: "rgl::standard::primitives::string", variablesReference: 50 } };
hoverResult.onDidSendMessage(evalString);
assert.equal(evalString.body.variablesReference, 0);
hoverResult.onWillReceiveMessage({ type: "request", seq: 3, command: "evaluate", arguments: { frameId: 6, expression: "p" } });
const evalMold = { type: "response", request_seq: 3, success: true, body: { result: "{rgl_f_nom:\"p\", rgl_f_x:1}", type: "rgl_m_Point", variablesReference: 51 } };
hoverResult.onDidSendMessage(evalMold);
assert.equal(evalMold.body.result, '{nom:"p", x:1}');
assert.equal(evalMold.body.variablesReference, 51);

console.log("debug-names : ok");
