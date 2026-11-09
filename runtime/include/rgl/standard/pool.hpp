#pragma once

// Un pool : un allocateur pour des blocs de quelques dizaines à quelques centaines d'octets qui naissent et meurent sans cesse
// (tampons de tableaux et de textes). Les blocs sont rangés par classe de taille ; un bloc rendu va en tête de la liste de sa classe, et la
// demande suivante de cette classe le reprend sans appeler le système. Rien n'est rendu au système avant la fin du programme,
// et il n'y a ni verrou ni attente : chaque thread a ses listes (un bloc libéré sur un autre thread que celui qui l'a pris rejoint les
// listes de ce thread-là, ce qui ne change rien à sa validité).
//
// Un pool n'est pas « scoped » : un bloc reste valable tant qu'on ne l'a pas rendu, à qui que ce soit, donc un tableau du pool peut
// sortir de la fonction qui l'a créé (contrairement à une arène). Chaque bloc porte un en-tête de 8 octets, sa classe ; l'adresse
// rendue est alignée sur 8 octets, ce que demandent tous les types de RGL. Un bloc plus grand que 2 Ko va au système.
//
// Se choisit par pool_allocator() : ArenaPool.set(valeur) du code généré ouvre une allocator_scope sur lui.

#include <cstddef>
#include <cstdint>
#include <cstdlib>
#include <cstring>

#include "rgl/standard/memory.hpp"

namespace rgl::standard {

namespace detail {

// Tailles utiles des classes, en octets.
inline constexpr std::size_t pool_class_sizes[] = {16, 32, 48, 64, 96, 128, 192, 256, 384, 512, 768, 1024, 1536, 2048};
inline constexpr unsigned pool_class_count = sizeof(pool_class_sizes) / sizeof(pool_class_sizes[0]);
inline constexpr std::size_t pool_header = 8;
inline constexpr std::size_t pool_chunk = 64 * 1024;

// L'en-tête d'un bloc : sa classe (petit bloc) ou sa taille (bloc donné au système), décalée d'un bit ; le bit de poids faible dit lequel.
inline constexpr std::uint64_t pool_large_bit = 1;

struct pool_free_block {
    pool_free_block* next;
};

struct pool_state {
    pool_free_block* free_blocks[pool_class_count] = {};
    char* bump = nullptr;
    char* end = nullptr;
};

inline thread_local pool_state pool_thread;

inline unsigned pool_class_of(std::size_t size) noexcept {
    for (unsigned i = 0; i < pool_class_count; ++i) {
        if (size <= pool_class_sizes[i]) return i;
    }
    return pool_class_count;
}

inline void* pool_allocate(const AbiAllocator&, std::size_t size) noexcept {
    const unsigned size_class = pool_class_of(size);
    if (size_class == pool_class_count) {
        auto* header = static_cast<std::uint64_t*>(std::malloc(size + pool_header));
        if (!header) return nullptr;
        *header = (static_cast<std::uint64_t>(size) << 1) | pool_large_bit;
        return header + 1;
    }

    pool_state& state = pool_thread;
    if (pool_free_block* block = state.free_blocks[size_class]) {
        state.free_blocks[size_class] = block->next;
        return block;
    }

    const std::size_t total = pool_header + pool_class_sizes[size_class];
    if (state.bump == nullptr || state.bump + total > state.end) {
        char* chunk = static_cast<char*>(std::malloc(pool_chunk));
        if (!chunk) return nullptr;
        state.bump = chunk;
        state.end = chunk + pool_chunk;
    }
    auto* header = reinterpret_cast<std::uint64_t*>(state.bump);
    state.bump += total;
    *header = static_cast<std::uint64_t>(size_class) << 1;
    return header + 1;
}

inline void pool_release(const AbiAllocator&, void* block) noexcept {
    if (!block) return;
    auto* header = static_cast<std::uint64_t*>(block) - 1;
    if (*header & pool_large_bit) {
        std::free(header);
        return;
    }
    auto* freed = static_cast<pool_free_block*>(block);
    pool_state& state = pool_thread;
    const auto size_class = static_cast<unsigned>(*header >> 1);
    freed->next = state.free_blocks[size_class];
    state.free_blocks[size_class] = freed;
}

inline void* pool_reallocate(const AbiAllocator& self, void* block, std::size_t size) noexcept {
    if (!block) return pool_allocate(self, size);
    auto* header = static_cast<std::uint64_t*>(block) - 1;
    const bool large = (*header & pool_large_bit) != 0;
    const std::size_t capacity = large ? static_cast<std::size_t>(*header >> 1) : pool_class_sizes[*header >> 1];

    if (large && pool_class_of(size) == pool_class_count) {
        auto* moved = static_cast<std::uint64_t*>(std::realloc(header, size + pool_header));
        if (!moved) return nullptr;
        *moved = (static_cast<std::uint64_t>(size) << 1) | pool_large_bit;
        return moved + 1;
    }
    if (!large && size <= capacity) return block;

    void* moved = pool_allocate(self, size);
    if (!moved) return nullptr;
    std::memcpy(moved, block, capacity < size ? capacity : size);
    pool_release(self, block);
    return moved;
}

}  // namespace detail

// L'allocateur du pool.
inline const AbiAllocator& pool_allocator() noexcept {
    static constexpr AbiAllocator instance{&detail::pool_allocate, &detail::pool_reallocate, &detail::pool_release};
    return instance;
}

}  // namespace rgl::standard
