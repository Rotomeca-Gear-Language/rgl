#pragma once

// Limite de profondeur des gears récursifs (clé « recursion » de rgl.json). Chaque gear récursif
// crée un recursion_guard en entrée : il compte les appels imbriqués des gears récursifs du
// thread, et arrête le programme, avec la position du gear, au-delà de la limite. Les gears non
// récursifs ne paient rien ; avec « recursion »: "infinite", aucun garde n'est créé.

#include <cstdint>
#include <cstdio>
#include <cstdlib>

namespace rgl::standard {

namespace detail {

inline thread_local std::uint32_t recursion_depth = 0;

[[noreturn]] inline void recursion_too_deep(const char* where, std::uint32_t limit) noexcept {
    std::fprintf(stderr, "rgl : récursion trop profonde, plus de %u appels imbriqués (%s)\n", static_cast<unsigned>(limit), where);
    std::abort();
}

}  // namespace detail

class recursion_guard {
   public:
    recursion_guard(std::uint32_t limit, const char* where) noexcept {
        if (++detail::recursion_depth > limit) detail::recursion_too_deep(where, limit);
    }

    ~recursion_guard() { --detail::recursion_depth; }

    recursion_guard(const recursion_guard&) = delete;
    recursion_guard& operator=(const recursion_guard&) = delete;
};

}  // namespace rgl::standard
