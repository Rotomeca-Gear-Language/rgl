#pragma once

#include <cstddef>

namespace rgl::standard {

// Allocateur à interface stable : trois pointeurs de fonction, sans dépendance à std::allocator. Chacun reçoit l'allocateur
// lui-même en premier argument, pour qu'un allocateur à état (une arène : une classe qui en dérive) retrouve le sien.
// Un objet qui garde un AbiAllocator* suppose que l'allocateur vit plus longtemps que lui :
// en pratique, ce sont des objets statiques (default_allocator(), allocateurs de tests…).
struct AbiAllocator {
    void* (*allocate)(const AbiAllocator& self, std::size_t size);
    void* (*reallocate)(const AbiAllocator& self, void* block, std::size_t size);
    void (*release)(const AbiAllocator& self, void* block);

    // Vrai pour une arène : la mémoire qu'elle donne n'est valable que jusqu'à sa remise à zéro. Un tableau ou un texte qui vit
    // dans une telle arène ne se copie ni ne se déplace jamais tel quel : la copie, ou le déplacement, est un objet du tas
    // (voir array.hpp et string.hpp), donc rien ne garde l'adresse d'une arène vidée. Un pool n'est pas « scoped » : sa mémoire reste
    // valable tant que le bloc n'est pas rendu, à qui que ce soit.
    bool scoped = false;
};

// Rend block à allocator ; réalloue block à size octets (nullptr si l'allocateur refuse : le bloc reste alors valide).
inline void release_block(const AbiAllocator& allocator, void* block) noexcept { allocator.release(allocator, block); }
inline void* try_reallocate(const AbiAllocator& allocator, void* block, std::size_t size) noexcept { return allocator.reallocate(allocator, block, size); }

// L'allocateur du système : malloc, realloc et free. Jamais celui d'un pool ni d'une arène : ce qui doit survivre à
// tout (un tableau qui sort d'une arène, une fonction partagée par compteur) s'y alloue.
const AbiAllocator& heap_allocator() noexcept;

namespace detail {

// L'allocateur ambiant du thread : celui de l'allocator_scope le plus récent, nullptr hors de tout bloc.
inline thread_local const AbiAllocator* ambient_allocator = nullptr;

}  // namespace detail

// L'allocateur des objets qu'on crée maintenant : celui du pool ou de l'arène ouvert sur ce thread, sinon celui du système.
// Un tableau, un texte ou un dictionnaire le retient à sa construction et s'en sert toute sa vie.
inline const AbiAllocator& default_allocator() noexcept {
    return detail::ambient_allocator ? *detail::ambient_allocator : heap_allocator();
}

// Fait de allocator l'allocateur ambiant du thread, jusqu'à la fin de la portée ; rétablit le précédent ensuite. Les portées
// s'emboîtent. allocator doit vivre plus longtemps que la portée (et que tout ce qu'il a alloué).
class allocator_scope {
   public:
    explicit allocator_scope(const AbiAllocator& allocator) noexcept : previous_(detail::ambient_allocator) { detail::ambient_allocator = &allocator; }
    allocator_scope(const allocator_scope&) = delete;
    allocator_scope& operator=(const allocator_scope&) = delete;
    ~allocator_scope() { detail::ambient_allocator = previous_; }

   private:
    const AbiAllocator* previous_;
};

// Compteurs de mesure, compilés seulement avec -DRGL_COUNT_ALLOCATIONS (compile --count-allocations) : le système compte ses blocs et les copies
// profondes d'un tableau ou d'un texte, et le programme les écrit sur la sortie d'erreur en s'arrêtant. Sans l'option, ce sont des fonctions vides :
// aucun coût dans un programme ordinaire.
namespace counters {

enum class copy_kind { array, text };

#ifdef RGL_COUNT_ALLOCATIONS
// Une copie profonde de `elements` éléments (des octets pour un texte).
void note_copy(copy_kind kind, std::size_t elements) noexcept;
#else
inline void note_copy(copy_kind, std::size_t) noexcept {}
#endif

}  // namespace counters

// Arrête le programme : la mémoire demandée n'a pas pu être obtenue. Le runtime ne continue
// jamais dans un état faux (tampon trop petit, chaîne vidée en silence).
[[noreturn]] void out_of_memory(std::size_t requested) noexcept;

// Alloue size octets avec allocator, ou arrête le programme.
void* allocate_or_abort(const AbiAllocator& allocator, std::size_t size) noexcept;

// Réalloue block à size octets avec allocator, ou arrête le programme.
void* reallocate_or_abort(const AbiAllocator& allocator, void* block, std::size_t size) noexcept;

}  // namespace rgl::standard
