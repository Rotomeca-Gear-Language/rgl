#pragma once

// Les intervalles d'entiers RGL ([0..10], [0..<n by 2], [10..0]) : une vue sans tableau, qui calcule
// chaque valeur à la demande. Elle se lit par size() et operator[] comme les tableaux : un parcours
// la lit directement, sans rien allouer, et un tableau s'en construit par from(). Tout est dans
// l'en-tête : un programme sans intervalle ne l'inclut pas.

#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstdlib>

namespace rgl::standard {

namespace detail {

// Assez large pour la distance entre deux entiers de 64 bits, signés ou non.
__extension__ typedef __int128 range_wide;

[[noreturn]] inline void zero_step(const char* where) noexcept {
    std::fprintf(stderr, "rgl : pas d'intervalle nul (%s)\n", where);
    std::abort();
}

}  // namespace detail

// Les valeurs first, first + step, first + 2 × step… : count valeurs, toutes entre les bornes de
// l'intervalle, donc toutes représentables en T.
template <class T>
class range_view {
public:
    // Une suite de T, comme les tableaux : ils s'en construisent par from().
    using rgl_sequence = T;

    constexpr range_view(T first, std::int64_t step, std::size_t count) noexcept : _first(first), _step(step), _count(count) {}

    constexpr std::size_t size() const noexcept { return _count; }

    constexpr T operator[](std::size_t index) const noexcept {
        return static_cast<T>(static_cast<detail::range_wide>(_first) + static_cast<detail::range_wide>(index) * _step);
    }

private:
    T _first;
    std::int64_t _step;
    std::size_t _count;
};

// L'intervalle de first à last par pas de step, last compris ou non (exclusive) : vide quand le pas
// va dans l'autre sens que les bornes. Un pas nul arrête le programme, à l'endroit where du source.
template <class T>
constexpr range_view<T> range(T first, T last, std::int64_t step, bool exclusive, const char* where) noexcept {
    using wide = detail::range_wide;
    if (step == 0) detail::zero_step(where);

    wide distance = step > 0 ? wide(last) - wide(first) : wide(first) - wide(last);
    if (exclusive) distance -= 1;
    if (distance < 0) return range_view<T>(first, step, 0);

    const wide magnitude = step > 0 ? wide(step) : -wide(step);
    return range_view<T>(first, step, static_cast<std::size_t>(distance / magnitude + 1));
}

}  // namespace rgl::standard
