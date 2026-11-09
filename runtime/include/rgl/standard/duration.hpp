#pragma once

// rgl::standard::duration : le type RGL duration, une durée en millisecondes sur 64 bits signés.
//
// Une durée s'additionne et se soustrait avec une durée ou un entier (des millisecondes), se multiplie
// ou se divise par un entier, et diviser une durée par une durée donne le nombre entier de
// fois (tronqué vers zéro) : `d / 1s` vaut les secondes entières. Un débordement ou une division par
// zéro arrête le programme, dans tous les modes (une durée n'a pas de roulement). Tout est constexpr
// tant que rien n'échoue.
//
// Une durée s'écrit en texte par ses millisecondes : `{5s}` donne « 5000ms ».

#include <compare>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <type_traits>

namespace rgl::standard {

namespace primitives {

namespace detail {

// Non constexpr : un échec pendant une évaluation à la compilation est une erreur de compilation.
[[noreturn]] inline void duration_failure(const char* message) noexcept {
    std::fprintf(stderr, "rgl : %s\n", message);
    std::abort();
}

}  // namespace detail

class duration {
   public:
    constexpr duration() noexcept = default;
    constexpr explicit duration(std::int64_t milliseconds) noexcept : milliseconds_(milliseconds) {}

    constexpr std::int64_t milliseconds() const noexcept { return milliseconds_; }

    friend constexpr bool operator==(duration left, duration right) noexcept = default;
    friend constexpr std::strong_ordering operator<=>(duration left, duration right) noexcept = default;

    friend constexpr duration operator+(duration left, duration right) noexcept {
        std::int64_t result;
        if (__builtin_add_overflow(left.milliseconds_, right.milliseconds_, &result)) detail::duration_failure("débordement de durée");
        return duration(result);
    }

    friend constexpr duration operator-(duration left, duration right) noexcept {
        std::int64_t result;
        if (__builtin_sub_overflow(left.milliseconds_, right.milliseconds_, &result)) detail::duration_failure("débordement de durée");
        return duration(result);
    }

    // Un entier s'ajoute ou se retranche comme des millisecondes.
    template <class N>
        requires std::is_integral_v<N>
    friend constexpr duration operator+(duration left, N count) noexcept {
        return left + duration(static_cast<std::int64_t>(count));
    }

    template <class N>
        requires std::is_integral_v<N>
    friend constexpr duration operator+(N count, duration right) noexcept {
        return duration(static_cast<std::int64_t>(count)) + right;
    }

    template <class N>
        requires std::is_integral_v<N>
    friend constexpr duration operator-(duration left, N count) noexcept {
        return left - duration(static_cast<std::int64_t>(count));
    }

    template <class N>
        requires std::is_integral_v<N>
    friend constexpr duration operator-(N count, duration right) noexcept {
        return duration(static_cast<std::int64_t>(count)) - right;
    }

    friend constexpr duration operator-(duration value) noexcept {
        return duration(0) - value;
    }

    template <class N>
        requires std::is_integral_v<N>
    friend constexpr duration operator*(duration left, N factor) noexcept {
        std::int64_t result;
        if (__builtin_mul_overflow(left.milliseconds_, static_cast<std::int64_t>(factor), &result)) detail::duration_failure("débordement de durée");
        return duration(result);
    }

    template <class N>
        requires std::is_integral_v<N>
    friend constexpr duration operator*(N factor, duration right) noexcept {
        return right * factor;
    }

    template <class N>
        requires std::is_integral_v<N>
    friend constexpr duration operator/(duration left, N divisor) noexcept {
        if (divisor == 0) detail::duration_failure("division entière par zéro");
        if (left.milliseconds_ == INT64_MIN && divisor == -1) detail::duration_failure("débordement de durée");
        return duration(left.milliseconds_ / static_cast<std::int64_t>(divisor));
    }

    // Combien de fois la seconde durée tient dans la première, tronqué vers zéro.
    friend constexpr std::int64_t operator/(duration left, duration right) noexcept {
        if (right.milliseconds_ == 0) detail::duration_failure("division entière par zéro");
        if (left.milliseconds_ == INT64_MIN && right.milliseconds_ == -1) detail::duration_failure("débordement de durée");
        return left.milliseconds_ / right.milliseconds_;
    }

   private:
    std::int64_t milliseconds_ = 0;
};

}  // namespace primitives

using primitives::duration;

}  // namespace rgl::standard
