#pragma once

// Les processus enfants : `spawn "cmake" "--build" ".";` lance un exécutable, attend sa fin et rend son code de sortie
// et ce qu'il a écrit. Rien ne s'arrête ni ne lève : un échec de lancement est un `outcome` dont `error` n'est pas `none`,
// que le code généré met dans un `SpawnError`. Le chemin de l'exécutable, les arguments, le dossier de travail et l'environnement
// sont en UTF-8 ; sous Windows ils sont convertis en UTF-16 pour l'API du système.
//
// Un programme RGL ne relance pas le compilateur : un exécutable nommé `rgl` ou `rgl.exe` est refusé (`permission_denied`).

#include <cstdint>
#include <utility>
#include <vector>

#include "rgl/standard/string.hpp"

namespace rgl::standard::process {

// Pourquoi un processus n'a pas pu s'exécuter jusqu'au bout.
enum class failure { none, exec_not_found, permission_denied, timeout, io_error };

// L'entrée standard du processus : vide (`null`, par défaut) ou fermée (`none`).
enum class input { null, none };

// Une sortie du processus : capturée dans le résultat (`pipe`, par défaut), partagée avec ce programme (`inherit`), ou jetée (`null`).
enum class output { pipe, inherit, null };

struct options {
    // Durée maximale en millisecondes ; 0 pour aucune. Passée, le processus est tué : `failure::timeout`.
    std::uint32_t timeout_ms = 0;
    // Dossier de travail ; vide pour celui de ce programme.
    string cwd;
    // Variables d'environnement ajoutées (ou remplacées) à celles de ce programme.
    std::vector<std::pair<string, string>> env;
    input in = input::null;
    output out = output::pipe;
    output err = output::pipe;
};

struct outcome {
    failure error = failure::none;
    // Le détail d'un échec, en français.
    string message;
    std::int32_t exit_code = 0;
    string out;
    string err;
};

// Lance `program` avec `args` (sans le nom du programme), attend sa fin.
outcome run(const string& program, const std::vector<string>& args, const options& opts);

}  // namespace rgl::standard::process
