#pragma once

// Conversions numériques de RGL (`as`, `as?`, `as!`) et contrôle des types bornés (`int<0..100>`).
// - Entier vers entier : la valeur doit tenir dans le type cible.
// - Flottant vers entier : la valeur est tronquée vers zéro, puis doit tenir dans le type ; NaN ne
//   tient jamais.
// - Vers un flottant : toujours possible (la valeur est arrondie au plus proche).
// Selon le cast, le code généré appelle checked_cast (arrêt du programme, avec l'endroit dans le
// source RGL), try_cast (null) ou fits (dans un guard, qui échoue alors). Tout est dans l'en-tête :
// un programme sans cast ni type borné ne l'inclut pas.

#include <concepts>
#include <cstdio>
#include <cstdlib>
#include <limits>
#include <type_traits>
#include <utility>

#include "rgl/standard/nullable.hpp"

namespace rgl::standard {

namespace detail {

// Volontairement non constexpr : une conversion qui échoue pendant une évaluation à la compilation
// est une erreur de compilation. Le compilateur RGL ne produit jamais une telle constante.
[[noreturn]] inline void out_of_range(const char* what, const char* where) noexcept {
    std::fprintf(stderr, "rgl : %s (%s)\n", what, where);
    std::abort();
}

}  // namespace detail

// Indique si `value` devient sans débordement une valeur de To.
template <class To, class From>
    requires(std::integral<To> || std::floating_point<To>) && (std::integral<From> || std::floating_point<From>)
constexpr bool fits(From value) noexcept {
    if constexpr (std::floating_point<To>) {
        return true;
    } else if constexpr (std::floating_point<From>) {
        // Tronquée vers zéro, la valeur tient si elle est dans ]min - 1, max + 1[. max + 1 est une
        // puissance de deux, exacte en flottant. min - 1 ne l'est pas toujours : arrondi, il vaut
        // min, d'où le second test ; aucun flottant n'existe alors entre min - 1 et min. NaN échoue
        // à toutes les comparaisons.
        constexpr From low = static_cast<From>(std::numeric_limits<To>::min());
        constexpr From high = static_cast<From>(std::numeric_limits<To>::max() / 2 + 1) * From(2);
        return (value > low - From(1) || value == low) && value < high;
    } else {
        return std::in_range<To>(value);
    }
}

// `v as T` contrôlé : la valeur convertie, ou l'arrêt du programme si elle ne tient pas.
template <class To, class From>
constexpr To checked_cast(From value, const char* what, const char* where) noexcept {
    if (!fits<To>(value)) detail::out_of_range(what, where);
    return static_cast<To>(value);
}

// `v as? T` : la valeur convertie, ou null si elle ne tient pas.
template <class To, class From>
constexpr nullable<To> try_cast(From value) noexcept {
    if (!fits<To>(value)) return std::nullopt;
    return static_cast<To>(value);
}

// Un double vers un float : arrondi au plus proche ; au-delà du plus grand float, l'infini du même
// signe (le C++ ne définit pas la conversion d'une valeur hors de l'étendue du type).
template <class To, class From>
    requires std::floating_point<To> && std::floating_point<From>
constexpr To narrow_float(From value) noexcept {
    if (value > static_cast<From>(std::numeric_limits<To>::max())) return std::numeric_limits<To>::infinity();
    if (value < static_cast<From>(std::numeric_limits<To>::lowest())) return -std::numeric_limits<To>::infinity();
    return static_cast<To>(value);
}

// Une valeur dans les bornes d'un type borné, bornes comprises ; NaN n'y est jamais.
template <class T>
constexpr bool in_bounds(T value, T min, T max) noexcept {
    return value >= min && value <= max;
}

// Une valeur qui entre dans un type borné : elle-même, ou l'arrêt du programme hors des bornes.
template <class T>
constexpr T checked_bounds(T value, T min, T max, const char* what, const char* where) noexcept {
    if (!in_bounds(value, min, max)) detail::out_of_range(what, where);
    return value;
}

// `v as? T<min..max>` : la valeur si elle est là et dans les bornes, null sinon.
template <class T>
constexpr nullable<T> try_bounds(const nullable<T>& value, T min, T max) noexcept {
    if (!value.has_value() || !in_bounds(*value, min, max)) return std::nullopt;
    return value;
}

}  // namespace rgl::standard
