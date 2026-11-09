#pragma once

// Arithmétique entière de RGL : les opérations qui ne débordent jamais silencieusement et ne
// tombent jamais dans un comportement indéfini du C++. Selon la clé « overflow » du projet, le
// code généré appelle les versions checked_ (arrêt du programme, avec l'endroit dans le source
// RGL) ou wrapping_ (roulement) :
// - addition, soustraction, multiplication, négation : débordement ;
// - division et reste : un diviseur nul arrête le programme dans les deux modes ; min / -1
//   déborde (checked) ou vaut min (wrapping) ;
// - décalages : un nombre de bits négatif ou au moins égal à la largeur arrête le programme
//   (checked) ou est réduit modulo la largeur (wrapping) ; les bits sortis ne comptent pas ;
// - increment, decrement et leurs formes post_ : comme l'addition et la soustraction.
// Le débordement d'un entier signé est indéfini en C++ : ces fonctions passent par les
// intrinsèques de Clang, définies dans tous les cas. Sur un flottant, elles calculent
// simplement (pas de débordement). Toutes sont constexpr, tant que rien n'échoue.

#include <cstdio>
#include "rgl/standard/position.hpp"
#include <cstdlib>
#include <limits>
#include <type_traits>

namespace rgl::standard {

namespace detail {

// Volontairement non constexpr : un débordement pendant une évaluation à la compilation est
// une erreur de compilation. Le compilateur RGL ne produit jamais une telle constante.
[[noreturn]] inline void integer_overflow(const char* where) noexcept {
    std::fprintf(stderr, "rgl : débordement d'entier (%s)\n", where);
    std::abort();
}

[[noreturn]] inline void division_by_zero(const char* where) noexcept {
    std::fprintf(stderr, "rgl : division entière par zéro (%s)\n", where);
    std::abort();
}

[[noreturn]] inline void shift_out_of_range(const char* where) noexcept {
    std::fprintf(stderr, "rgl : décalage hors limites (%s)\n", where);
    std::abort();
}

// Les mêmes, pour une position tenue dans 32 bits (voir position.hpp).
[[noreturn]] inline void integer_overflow(position where) noexcept {
    char buffer[320];
    integer_overflow(position_text(where, buffer));
}

[[noreturn]] inline void division_by_zero(position where) noexcept {
    char buffer[320];
    division_by_zero(position_text(where, buffer));
}

[[noreturn]] inline void shift_out_of_range(position where) noexcept {
    char buffer[320];
    shift_out_of_range(position_text(where, buffer));
}

// Nombre de bits d'un décalage, vérifié : entre 0 et la largeur de T exclue.
template <class T, class N, class W>
constexpr int checked_shift_bits(N bits, W where) noexcept {
    constexpr N width = static_cast<N>(sizeof(T) * 8);
    if constexpr (std::is_signed_v<N>) {
        if (bits < 0) shift_out_of_range(where);
    }
    if (bits >= width) shift_out_of_range(where);
    return static_cast<int>(bits);
}

// Nombre de bits d'un décalage, réduit modulo la largeur de T (une puissance de deux).
template <class T, class N>
constexpr int wrapping_shift_bits(N bits) noexcept {
    return static_cast<int>(static_cast<std::make_unsigned_t<N>>(bits) & (sizeof(T) * 8 - 1));
}

// Décalages sur des bits déjà vérifiés : à gauche par le type non signé (les bits sortis sont
// perdus, jamais un débordement), à droite arithmétique pour un signé.
template <class T>
constexpr T shift_left(T value, int bits) noexcept {
    return static_cast<T>(static_cast<std::make_unsigned_t<T>>(value) << bits);
}

template <class T>
constexpr T shift_right(T value, int bits) noexcept {
    return static_cast<T>(value >> bits);
}

}  // namespace detail

template <class T, class W>
constexpr T checked_add(T left, T right, W where) noexcept {
    if constexpr (std::is_floating_point_v<T>) {
        return left + right;
    } else {
        T result{};
        if (__builtin_add_overflow(left, right, &result)) detail::integer_overflow(where);
        return result;
    }
}

template <class T, class W>
constexpr T checked_sub(T left, T right, W where) noexcept {
    if constexpr (std::is_floating_point_v<T>) {
        return left - right;
    } else {
        T result{};
        if (__builtin_sub_overflow(left, right, &result)) detail::integer_overflow(where);
        return result;
    }
}

template <class T, class W>
constexpr T checked_mul(T left, T right, W where) noexcept {
    T result{};
    if (__builtin_mul_overflow(left, right, &result)) detail::integer_overflow(where);
    return result;
}

// Vérification groupée : le calcul se fait sans test à chaque opération (Clang peut alors vectoriser la boucle), le débordement
// s'accumule dans un drapeau et un seul test suit la boucle. Réservée aux étapes sans effet d'un parcours : rien n'est visible
// entre le premier débordement et l'arrêt.
// Jusqu'à 32 bits, le calcul se fait dans un entier de 64 bits et le débordement est une troncature qui change la valeur : de
// simples instructions vectorielles, alors que les intrinsèques « with_overflow » de Clang ne se vectorisent pas.
// À 64 bits, les intrinsèques restent (le calcul sur 128 bits ne se vectorise pas non plus).
namespace detail {

template <class T>
using grouped_wide = std::conditional_t<std::is_signed_v<T>, long long, unsigned long long>;

template <class T>
constexpr T grouped_narrow(grouped_wide<T> wide, bool& overflowed) noexcept {
    const T result = static_cast<T>(wide);
    overflowed |= static_cast<grouped_wide<T>>(result) != wide;
    return result;
}

}  // namespace detail

template <class T>
constexpr T grouped_add(T left, T right, bool& overflowed) noexcept {
    if constexpr (std::is_floating_point_v<T>) {
        return left + right;
    } else if constexpr (sizeof(T) < 8) {
        using W = detail::grouped_wide<T>;
        return detail::grouped_narrow<T>(static_cast<W>(left) + static_cast<W>(right), overflowed);
    } else {
        T result{};
        overflowed |= __builtin_add_overflow(left, right, &result);
        return result;
    }
}

template <class T>
constexpr T grouped_sub(T left, T right, bool& overflowed) noexcept {
    if constexpr (std::is_floating_point_v<T>) {
        return left - right;
    } else if constexpr (sizeof(T) < 8) {
        using W = detail::grouped_wide<T>;
        return detail::grouped_narrow<T>(static_cast<W>(left) - static_cast<W>(right), overflowed);
    } else {
        T result{};
        overflowed |= __builtin_sub_overflow(left, right, &result);
        return result;
    }
}

template <class T>
constexpr T grouped_mul(T left, T right, bool& overflowed) noexcept {
    if constexpr (std::is_floating_point_v<T>) {
        return left * right;
    } else if constexpr (sizeof(T) < 8) {
        using W = detail::grouped_wide<T>;
        return detail::grouped_narrow<T>(static_cast<W>(left) * static_cast<W>(right), overflowed);
    } else {
        T result{};
        overflowed |= __builtin_mul_overflow(left, right, &result);
        return result;
    }
}

/// Le test unique qui suit une boucle à vérification groupée.
template <class W>
inline void check_grouped_overflow(bool overflowed, W where) noexcept {
    if (overflowed) detail::integer_overflow(where);
}

template <class T, class W>
constexpr T checked_negate(T value, W where) noexcept {
    return checked_sub<T>(T{0}, value, where);
}

template <class T>
constexpr T wrapping_add(T left, T right) noexcept {
    if constexpr (std::is_floating_point_v<T>) {
        return left + right;
    } else {
        T result{};
        static_cast<void>(__builtin_add_overflow(left, right, &result));
        return result;
    }
}

template <class T>
constexpr T wrapping_sub(T left, T right) noexcept {
    if constexpr (std::is_floating_point_v<T>) {
        return left - right;
    } else {
        T result{};
        static_cast<void>(__builtin_sub_overflow(left, right, &result));
        return result;
    }
}

template <class T>
constexpr T wrapping_mul(T left, T right) noexcept {
    T result{};
    static_cast<void>(__builtin_mul_overflow(left, right, &result));
    return result;
}

template <class T>
constexpr T wrapping_negate(T value) noexcept {
    return wrapping_sub<T>(T{0}, value);
}

// --- Division et reste ---------------------------------------------------------------------

template <class T, class W>
constexpr T checked_div(T left, T right, W where) noexcept {
    if (right == 0) detail::division_by_zero(where);
    if constexpr (std::is_signed_v<T>) {
        if (right == T(-1) && left == std::numeric_limits<T>::min()) detail::integer_overflow(where);
    }
    return static_cast<T>(left / right);
}

template <class T, class W>
constexpr T wrapping_div(T left, T right, W where) noexcept {
    if (right == 0) detail::division_by_zero(where);
    if constexpr (std::is_signed_v<T>) {
        if (right == T(-1)) return wrapping_negate<T>(left);  // min / -1 fait le tour : min
    }
    return static_cast<T>(left / right);
}

// Le reste ne déborde jamais : min % -1 vaut 0 (mais le calculer est indéfini en C++).
template <class T, class W>
constexpr T checked_rem(T left, T right, W where) noexcept {
    if (right == 0) detail::division_by_zero(where);
    if constexpr (std::is_signed_v<T>) {
        if (right == T(-1)) return T{0};
    }
    return static_cast<T>(left % right);
}

template <class T, class W>
constexpr T wrapping_rem(T left, T right, W where) noexcept {
    return checked_rem<T>(left, right, where);
}

// --- Décalages -------------------------------------------------------------------------------

template <class T, class N, class W>
constexpr T checked_shl(T value, N bits, W where) noexcept {
    return detail::shift_left<T>(value, detail::checked_shift_bits<T>(bits, where));
}

template <class T, class N, class W>
constexpr T checked_shr(T value, N bits, W where) noexcept {
    return detail::shift_right<T>(value, detail::checked_shift_bits<T>(bits, where));
}

template <class T, class N>
constexpr T wrapping_shl(T value, N bits) noexcept {
    return detail::shift_left<T>(value, detail::wrapping_shift_bits<T>(bits));
}

template <class T, class N>
constexpr T wrapping_shr(T value, N bits) noexcept {
    return detail::shift_right<T>(value, detail::wrapping_shift_bits<T>(bits));
}

// --- increment, decrement et leurs formes post_ ---------------------------------------------
// Modifient la variable ; renvoient sa nouvelle valeur, ou son ancienne pour post_.

template <class T, class W>
constexpr T checked_increment(T& variable, T step, W where) noexcept {
    variable = checked_add<T>(variable, step, where);
    return variable;
}

template <class T, class W>
constexpr T checked_decrement(T& variable, T step, W where) noexcept {
    variable = checked_sub<T>(variable, step, where);
    return variable;
}

template <class T, class W>
constexpr T checked_post_increment(T& variable, T step, W where) noexcept {
    const T old = variable;
    variable = checked_add<T>(variable, step, where);
    return old;
}

template <class T, class W>
constexpr T checked_post_decrement(T& variable, T step, W where) noexcept {
    const T old = variable;
    variable = checked_sub<T>(variable, step, where);
    return old;
}

template <class T>
constexpr T wrapping_increment(T& variable, T step) noexcept {
    variable = wrapping_add<T>(variable, step);
    return variable;
}

template <class T>
constexpr T wrapping_decrement(T& variable, T step) noexcept {
    variable = wrapping_sub<T>(variable, step);
    return variable;
}

template <class T>
constexpr T wrapping_post_increment(T& variable, T step) noexcept {
    const T old = variable;
    variable = wrapping_add<T>(variable, step);
    return old;
}

template <class T>
constexpr T wrapping_post_decrement(T& variable, T step) noexcept {
    const T old = variable;
    variable = wrapping_sub<T>(variable, step);
    return old;
}

}  // namespace rgl::standard
