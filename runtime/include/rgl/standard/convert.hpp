#pragma once

// Lecture d'un nombre dans une chaîne, pour `texte as! int` et `texte as? int` : la chaîne entière
// doit être le nombre, dans les limites du type, sans espace autour. Tout est dans l'en-tête : un programme
// sans `as!` ne l'inclut pas.

#include <charconv>
#include <concepts>
#include <system_error>

#include "rgl/standard/format.hpp"
#include "rgl/standard/nullable.hpp"
#include "rgl/standard/string.hpp"

namespace rgl::standard {

// Lit dans `text` un entier en décimal (signe « - » permis pour un type signé) ou un flottant
// (« 3.25 », « 1e-3 ») ; renvoie faux si la chaîne n'est pas entièrement un nombre du type.
template <class T>
    requires(std::integral<T> || std::floating_point<T>) && (!std::same_as<T, bool>)
bool parse_number(const string& text, T& out) noexcept {
    const char* first = text.data();
    const char* last = first + text.size();
    if (first == last) return false;

    const auto result = std::from_chars(first, last, out);
    return result.ec == std::errc{} && result.ptr == last;
}

// `texte as? int` : le nombre, ou null si le texte n'en est pas entièrement un.
template <class T>
    requires(std::integral<T> || std::floating_point<T>) && (!std::same_as<T, bool>)
nullable<T> try_parse_number(const string& text) noexcept {
    T value{};
    if (parse_number(text, value)) return value;
    return std::nullopt;
}

// Le message de l'échec d'un `as!` (une CastError) : « « abc » n'est pas un int ».
inline string cast_message(const string& text, const char* type) {
    return interpolate(string("\302\253 "), text, string(" \302\273 n'est pas un "), string(type));
}

}  // namespace rgl::standard
