#include "rgl/standard/memory.hpp"

#include <cstdio>
#include <cstdlib>

#ifdef RGL_COUNT_ALLOCATIONS
#include <atomic>
#endif

namespace rgl::standard {

namespace {

#ifdef RGL_COUNT_ALLOCATIONS
struct counting_state {
    std::atomic<unsigned long long> allocations{0}, bytes{0}, reallocations{0}, releases{0};
    std::atomic<unsigned long long> array_copies{0}, array_elements{0}, text_copies{0}, text_bytes{0};

    ~counting_state() {
        std::fprintf(stderr,
                     "rgl-compteurs : allocations=%llu octets=%llu reallocations=%llu liberations=%llu copies_tableaux=%llu elements_copies=%llu copies_textes=%llu octets_textes=%llu\n",
                     allocations.load(), bytes.load(), reallocations.load(), releases.load(), array_copies.load(), array_elements.load(), text_copies.load(),
                     text_bytes.load());
    }
};

// Créé à la première demande, détruit à la fin du programme : l'écriture du bilan est son destructeur.
counting_state& counting() {
    static counting_state state;
    return state;
}

void* allocate_default(const AbiAllocator&, std::size_t size) {
    auto& state = counting();
    state.allocations.fetch_add(1, std::memory_order_relaxed);
    state.bytes.fetch_add(size, std::memory_order_relaxed);
    return std::malloc(size);
}
void* reallocate_default(const AbiAllocator&, void* block, std::size_t size) {
    counting().reallocations.fetch_add(1, std::memory_order_relaxed);
    return std::realloc(block, size);
}
void release_default(const AbiAllocator&, void* block) {
    counting().releases.fetch_add(1, std::memory_order_relaxed);
    std::free(block);
}
#else
void* allocate_default(const AbiAllocator&, std::size_t size) { return std::malloc(size); }
void* reallocate_default(const AbiAllocator&, void* block, std::size_t size) { return std::realloc(block, size); }
void release_default(const AbiAllocator&, void* block) { std::free(block); }
#endif

constexpr AbiAllocator default_instance{&allocate_default, &reallocate_default, &release_default};

}  // namespace

const AbiAllocator& heap_allocator() noexcept { return default_instance; }

#ifdef RGL_COUNT_ALLOCATIONS
namespace counters {

void note_copy(copy_kind kind, std::size_t elements) noexcept {
    auto& state = counting();
    if (kind == copy_kind::array) {
        state.array_copies.fetch_add(1, std::memory_order_relaxed);
        state.array_elements.fetch_add(elements, std::memory_order_relaxed);
    } else {
        state.text_copies.fetch_add(1, std::memory_order_relaxed);
        state.text_bytes.fetch_add(elements, std::memory_order_relaxed);
    }
}

}  // namespace counters
#endif

void out_of_memory(std::size_t requested) noexcept {
    std::fprintf(stderr, "rgl : mémoire insuffisante (%zu octets demandés), arrêt du programme\n", requested);
    std::abort();
}

void* allocate_or_abort(const AbiAllocator& allocator, std::size_t size) noexcept {
    void* block = allocator.allocate(allocator, size);
    if (!block) out_of_memory(size);
    return block;
}

void* reallocate_or_abort(const AbiAllocator& allocator, void* block, std::size_t size) noexcept {
    void* moved = allocator.reallocate(allocator, block, size);
    if (!moved) out_of_memory(size);
    return moved;
}

}  // namespace rgl::standard
