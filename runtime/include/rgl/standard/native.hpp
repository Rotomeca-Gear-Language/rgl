#pragma once

// Passage des valeurs RGL aux fonctions C d'un fichier `forge: native`. Tout est dans l'en-tête : un programme sans
// fonction C ne l'inclut pas.

#include <type_traits>

namespace rgl::standard::native {

// Une valeur d'enum RGL donnée à une fonction C. Le paramètre C est une enum que le C++ ne construit pas depuis un entier,
// et le code généré ne connaît pas son nom : le proxy se convertit en la bonne enum, quelle qu'elle soit.
struct enum_value {
    long long value;

    template <class E>
        requires std::is_enum_v<E>
    constexpr operator E() const noexcept {
        return static_cast<E>(value);
    }
};

}  // namespace rgl::standard::native

// L'appel protégé : les exceptions que lance le C++ d'une liaison ne traversent jamais le code du programme, compilé sans exceptions (elles
// y arrêteraient le programme : std::terminate). `body` est du code compilé AVEC les exceptions (le C++ de la liaison, ou une fonction qui
// l'appelle) ; `rgl_native_guard`, dans native.cpp, l'appelle et rend un statut :
//   0 : `body` a abouti ;
//   1 : une exception, dont `status->message` donne le texte (`what()` d'une std::exception, sinon « exception C++ inconnue »).
// Le texte est tronqué à la taille du tampon. Le programme n'a que ce composant à lier : sans C++ natif, rien de tout cela n'est compilé.
extern "C" {

struct rgl_native_status {
    int failed;
    char message[256];
};

int rgl_native_guard(void (*body)(void*), void* context, rgl_native_status* status) noexcept;

}  // extern "C"

namespace rgl::standard::native {

// Le texte d'une exception qui n'est pas une std::exception (par exemple `throw 42`).
inline constexpr const char* unknown_exception = "exception C++ inconnue";

}  // namespace rgl::standard::native
