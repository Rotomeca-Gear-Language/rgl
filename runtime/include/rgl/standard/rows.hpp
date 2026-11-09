#pragma once

// Le nombre de lignes d'un soa mold parcouru par |S|> ou ||S|>.

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstdlib>

namespace rgl::standard {

namespace detail {

[[noreturn]] inline void rows_differ(const char* where) noexcept {
    std::fprintf(stderr, "rgl : les colonnes n'ont pas la même taille (%s)\n", where);
    std::abort();
}

}  // namespace detail

/// La taille des colonnes : toutes la même pour `|S|>` (le programme s'arrête sinon), la plus petite pour `||S|>`.
template <class... Sizes>
std::int64_t row_count(bool strict, const char* where, std::size_t first, Sizes... others) noexcept {
    std::size_t result = first;
    for (const std::size_t size : {static_cast<std::size_t>(others)...}) {
        if (strict) {
            if (size != first) detail::rows_differ(where);
        } else {
            result = std::min(result, size);
        }
    }
    return static_cast<std::int64_t>(result);
}

template <>
inline std::int64_t row_count(bool, const char*, std::size_t first) noexcept {
    return static_cast<std::int64_t>(first);
}

}  // namespace rgl::standard
