#pragma once

// Une arène : de la mémoire qu'on donne en avançant un pointeur, et qu'on reprend d'un coup. Allouer coûte quelques instructions,
// rendre un bloc ne coûte rien (il reste à sa place jusqu'à la remise à zéro), et ce qui a été créé ensemble est voisin en mémoire.
//
// L'arène (`arena_scope`, ce que le code généré ouvre pour un bloc `arena { … }`) est l'allocateur ambiant du thread jusqu'à la fin du
// bloc : tout tableau, texte ou dictionnaire créé alors y est alloué, y compris par les gears appelés. À la fin du bloc, l'arène est remise à
// zéro ; elle garde son premier segment de mémoire, qu'un autre bloc `arena` réutilise (un tour de boucle ne demande rien au système).
//
// Sûreté : l'allocateur d'une arène est « scoped ». Un tableau ou un texte qui vit dans une arène ne se copie ni ne se déplace tel quel
// (array.hpp, string.hpp) : la copie, ou le déplacement, est un objet du tas. Les points qui échappent à cette règle sont les retours de
// valeur par élision (`return x;` construit la valeur dans l'appelant sans la déplacer) : `detach` les referme (le code généré l'écrit à
// chaque sortie de valeur d'un bloc arena). Aucune valeur ne garde donc l'adresse d'une arène remise à zéro.
//
// Chaque bloc porte un en-tête de 8 octets (sa taille), pour réallouer : un bloc qui est le dernier de son segment grandit sur place (la
// croissance d'un tableau en bout d'arène ne copie rien), sinon un nouveau bloc est pris et l'ancien reste jusqu'à la remise à zéro.

#include <cstddef>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <type_traits>

#include "rgl/standard/memory.hpp"

namespace rgl::standard {

class arena {
   public:
    // Taille du premier segment de mémoire (au moins 4 Ko, 64 Ko en général) ; les suivants doublent (jusqu'à 16 Mo), un bloc plus grand a le sien.
    explicit arena(std::size_t first_segment = 64 * 1024) noexcept : first_size_(first_segment < 4096 ? 4096 : first_segment), allocator_(this) {}

    arena(const arena&) = delete;
    arena& operator=(const arena&) = delete;

    ~arena() { _free_segments(first_); }

    // L'allocateur de l'arène (scoped).
    const AbiAllocator& allocator() const noexcept { return allocator_; }

    // Rend la mémoire d'un coup : ne reste que le premier segment, vide.
    void reset() noexcept {
        if (first_ == nullptr) return;
        _free_segments(first_->next);
        first_->next = nullptr;
        first_->used = 0;
        current_ = first_;
    }

    // Demande un premier segment d'au moins first_segment octets pour la prochaine fois (la mémoire déjà prise ne change pas).
    void want_first_segment(std::size_t first_segment) noexcept {
        if (first_segment > first_size_) first_size_ = first_segment;
    }

    // Chaînage des arènes disponibles (voir arena_scope).
    arena* next_free = nullptr;

   private:
    struct segment {
        segment* next;
        std::size_t capacity;
        std::size_t used;
    };

    // L'allocateur : une AbiAllocator qui retrouve son arène.
    struct arena_allocator : AbiAllocator {
        explicit arena_allocator(arena* owner_) noexcept
            : AbiAllocator{&arena_allocator::allocate_thunk, &arena_allocator::reallocate_thunk, &arena_allocator::release_thunk, true}, owner(owner_) {}

        arena* owner;

        static void* allocate_thunk(const AbiAllocator& self, std::size_t size) noexcept { return static_cast<const arena_allocator&>(self).owner->_allocate(size); }
        static void* reallocate_thunk(const AbiAllocator& self, void* block, std::size_t size) noexcept {
            return static_cast<const arena_allocator&>(self).owner->_reallocate(block, size);
        }
        static void release_thunk(const AbiAllocator&, void*) noexcept {}
    };

    static constexpr std::size_t header_size = 8;
    static constexpr std::size_t max_segment = 16u * 1024 * 1024;

    segment* first_ = nullptr;
    segment* current_ = nullptr;
    std::size_t first_size_;
    arena_allocator allocator_;

    static std::size_t _round(std::size_t size) noexcept { return (size + 7) & ~static_cast<std::size_t>(7); }
    static char* _data(segment* s) noexcept { return reinterpret_cast<char*>(s + 1); }

    static void _free_segments(segment* s) noexcept {
        while (s) {
            segment* next = s->next;
            std::free(s);
            s = next;
        }
    }

    // Un segment d'au moins needed octets de données, relié à la fin de la chaîne.
    segment* _grow(std::size_t needed) noexcept {
        std::size_t capacity = first_ == nullptr ? first_size_ : (current_->capacity * 2 < max_segment ? current_->capacity * 2 : max_segment);
        if (capacity < needed) capacity = needed;
        auto* fresh = static_cast<segment*>(std::malloc(sizeof(segment) + capacity));
        if (!fresh) return nullptr;
        fresh->next = nullptr;
        fresh->capacity = capacity;
        fresh->used = 0;
        if (first_ == nullptr) first_ = fresh;
        else current_->next = fresh;
        current_ = fresh;
        return fresh;
    }

    void* _allocate(std::size_t size) noexcept {
        const std::size_t total = header_size + _round(size);
        segment* s = current_;
        if (s == nullptr || s->used + total > s->capacity) {
            s = _grow(total);
            if (!s) return nullptr;
        }
        char* base = _data(s) + s->used;
        s->used += total;
        *reinterpret_cast<std::uint64_t*>(base) = size;
        return base + header_size;
    }

    void* _reallocate(void* block, std::size_t size) noexcept {
        if (!block) return _allocate(size);
        auto* header = static_cast<std::uint64_t*>(block) - 1;
        const std::size_t old_size = static_cast<std::size_t>(*header);
        if (size <= old_size) return block;

        // Le dernier bloc du segment courant grandit sur place s'il y a la place.
        segment* s = current_;
        if (s != nullptr) {
            char* block_end = static_cast<char*>(block) + _round(old_size);
            if (block_end == _data(s) + s->used && static_cast<char*>(block) >= _data(s)) {
                const std::size_t extra = _round(size) - _round(old_size);
                if (s->used + extra <= s->capacity) {
                    s->used += extra;
                    *header = size;
                    return block;
                }
            }
        }

        void* moved = _allocate(size);
        if (!moved) return nullptr;
        std::memcpy(moved, block, old_size);
        return moved;
    }
};

namespace detail {

// Les arènes que les blocs ont rendues, prêtes à servir : un bloc arena en prend une, la rend vidée.
inline thread_local arena* arena_free_list = nullptr;

}  // namespace detail

// Ouvre une arène pour la durée d'un bloc : elle devient l'allocateur ambiant du thread, et la mémoire est rendue à la sortie (par
// return, fail, break ou la fin normale, indifféremment). Les blocs emboîtés ont chacun leur arène.
class arena_scope {
   public:
    // first_segment : la taille voulue du premier segment (0 : celle par défaut).
    explicit arena_scope(std::size_t first_segment = 0) : arena_(_acquire(first_segment)), scope_(arena_->allocator()) {}

    arena_scope(const arena_scope&) = delete;
    arena_scope& operator=(const arena_scope&) = delete;

    ~arena_scope() {
        arena_->reset();
        arena_->next_free = detail::arena_free_list;
        detail::arena_free_list = arena_;
    }

   private:
    arena* arena_;
    allocator_scope scope_;

    static arena* _acquire(std::size_t first_segment) {
        arena* taken = detail::arena_free_list;
        if (taken != nullptr) {
            detail::arena_free_list = taken->next_free;
            taken->next_free = nullptr;
            taken->want_first_segment(first_segment);
            return taken;
        }
        return new arena(first_segment == 0 ? 64 * 1024 : first_segment);
    }
};

// Une valeur qui sort d'un bloc arena : un déplacement (une copie si value est une lvalue) qui passe par le constructeur, donc par les
// règles des tableaux et des textes, au lieu d'être élidé. Le résultat est sur le tas si la valeur était dans l'arène.
template <class T>
std::remove_cvref_t<T> detach(T&& value) {
    using V = std::remove_cvref_t<T>;
    // Le tas est l'allocateur ambiant pendant la construction : ni la copie ni le déplacement ne peuvent rester dans l'arène.
    allocator_scope heap_scope(heap_allocator());
    if constexpr (std::is_lvalue_reference_v<T>) return V(value);
    else return V(static_cast<V&&>(value));
}

}  // namespace rgl::standard
