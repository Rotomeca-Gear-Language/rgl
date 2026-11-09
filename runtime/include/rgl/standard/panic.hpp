#pragma once

// L'instruction panic de RGL : arrête le programme sur-le-champ, avec le code de sortie 101. Le
// message part sur stderr, suivi de la position du panic ; la console (console.hpp) est vidée avant
// de quitter par ses propres gestionnaires de sortie. Aucune exception C++ : std::exit.

#include <cstdio>
#include <cstdlib>

#include "string.hpp"

namespace rgl::standard {

inline constexpr int panic_exit_code = 101;

// `where` est nul quand le message porte déjà la position (une erreur écrite en texte).
[[noreturn]] inline void panic(const string& message, const char* where) noexcept {
    if (where != nullptr) std::fprintf(stderr, "rgl : panic : %s (%s)\n", message.data(), where);
    else std::fprintf(stderr, "rgl : panic : %s\n", message.data());
    std::fflush(stderr);
    std::exit(panic_exit_code);
}

}  // namespace rgl::standard
