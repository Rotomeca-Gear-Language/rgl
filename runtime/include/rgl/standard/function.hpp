#pragma once

// rgl::standard::function<R(Args...)>, le type d'une lambda RGL : (int, string) => bool.
//
// Une lambda RGL capture par copie. Son objet peut modifier le contenu explicitement mut de cette
// copie entre deux appels. Une petite lambda (jusqu'à trois pointeurs, déplaçable sans échec)
// est rangée dans la function elle-même, sans allocation ; une plus grande va dans un bloc du tas,
// alloué par un AbiAllocator et partagé par les copies (compteur de références atomique), puisque
// personne ne peut le modifier. Une function n'est jamais vide : RGL n'a pas de lambda nulle hors
// d'un T?, qui est un nullable<function<…>>. Ni exception, ni RTTI.

#include <atomic>
#include <cstddef>
#include <new>
#include <type_traits>

#include "memory.hpp"

namespace rgl::standard {

template <class Signature>
class function;

template <class R, class... Args>
class function<R(Args...)> {
public:
    // Toute valeur appelable avec ces arguments et qui donne un R : une lambda C++, un pointeur de
    // fonction (un gear passé par son nom).
    template <class F>
        requires(!std::is_same_v<std::remove_cvref_t<F>, function> && std::is_invocable_r_v<R, std::remove_cvref_t<F>&, Args...>)
    function(F&& callable, const AbiAllocator& allocator = heap_allocator()) noexcept {
        using Callable = std::remove_cvref_t<F>;
        // Une lambda peut sortir du bloc qui l'a créée : ce qu'elle capture (tableaux, textes) est construit avec le tas pour allocateur ambiant,
        // donc recopié sur le tas si cela vient d'une arène.
        allocator_scope heap_scope(heap_allocator());
        if constexpr (_fits_inline<Callable>) {
            ::new (static_cast<void*>(_storage)) Callable(static_cast<F&&>(callable));
            _operations = &_inline_operations<Callable>;
        } else {
            static_assert(alignof(Callable) <= alignof(std::max_align_t), "lambda trop alignée pour le tas");
            auto* block = static_cast<_Block<Callable>*>(allocate_or_abort(allocator, sizeof(_Block<Callable>)));
            ::new (static_cast<void*>(block)) _Block<Callable>{{1, &allocator}, Callable(static_cast<F&&>(callable))};
            ::new (static_cast<void*>(_storage)) _Block<Callable>*(block);
            _operations = &_heap_operations<Callable>;
        }
    }

    function(const function& other) noexcept : _operations(other._operations) {
        allocator_scope heap_scope(heap_allocator());
        _operations->copy(other._storage, _storage);
    }

    function(function&& other) noexcept : _operations(other._operations) {
        allocator_scope heap_scope(heap_allocator());
        _operations->move(other._storage, _storage);
    }

    function& operator=(const function& other) noexcept {
        allocator_scope heap_scope(heap_allocator());
        if (this != &other) {
            _operations->destroy(_storage);
            _operations = other._operations;
            _operations->copy(other._storage, _storage);
        }
        return *this;
    }

    function& operator=(function&& other) noexcept {
        allocator_scope heap_scope(heap_allocator());
        if (this != &other) {
            _operations->destroy(_storage);
            _operations = other._operations;
            _operations->move(other._storage, _storage);
        }
        return *this;
    }

    ~function() { _operations->destroy(_storage); }

    // L'appel : les arguments sont transmis tels quels.
    R operator()(Args... args) const { return _operations->call(_storage, static_cast<Args&&>(args)...); }

    // Vrai si la lambda est rangée sur le tas (pour les tests).
    bool on_heap() const noexcept { return _operations->on_heap; }

private:
    static constexpr std::size_t _inline_size = 3 * sizeof(void*);

    template <class F>
    static constexpr bool _fits_inline =
        sizeof(F) <= _inline_size && alignof(F) <= alignof(void*) && std::is_nothrow_move_constructible_v<F> && std::is_nothrow_copy_constructible_v<F>;

    // Ce que fait la function selon la lambda rangée : une table par type de lambda et par rangement.
    struct _Operations {
        R (*call)(const unsigned char* storage, Args&&... args);
        void (*copy)(const unsigned char* from, unsigned char* to) noexcept;
        void (*move)(unsigned char* from, unsigned char* to) noexcept;
        void (*destroy)(unsigned char* storage) noexcept;
        bool on_heap;
    };

    struct _Header {
        std::atomic<std::size_t> references;
        const AbiAllocator* allocator;
    };

    template <class F>
    struct _Block {
        _Header header;
        F callable;
    };

    template <class F>
    static const F& _stored(const unsigned char* storage) noexcept {
        return *std::launder(reinterpret_cast<const F*>(storage));
    }

    template <class F>
    static F& _stored_mut(const unsigned char* storage) noexcept {
        return *std::launder(reinterpret_cast<F*>(const_cast<unsigned char*>(storage)));
    }

    template <class F>
    static constexpr _Operations _inline_operations{
        [](const unsigned char* storage, Args&&... args) -> R { return _stored_mut<F>(storage)(static_cast<Args&&>(args)...); },
        [](const unsigned char* from, unsigned char* to) noexcept { ::new (static_cast<void*>(to)) F(_stored<F>(from)); },
        [](unsigned char* from, unsigned char* to) noexcept {
            ::new (static_cast<void*>(to)) F(static_cast<F&&>(*std::launder(reinterpret_cast<F*>(from))));
        },
        [](unsigned char* storage) noexcept { std::launder(reinterpret_cast<F*>(storage))->~F(); },
        false,
    };

    template <class F>
    static _Block<F>* _block(const unsigned char* storage) noexcept {
        return *std::launder(reinterpret_cast<_Block<F>* const*>(storage));
    }

    template <class F>
    static constexpr _Operations _heap_operations{
        [](const unsigned char* storage, Args&&... args) -> R { return _block<F>(storage)->callable(static_cast<Args&&>(args)...); },
        [](const unsigned char* from, unsigned char* to) noexcept {
            auto* block = _block<F>(from);
            block->header.references.fetch_add(1, std::memory_order_relaxed);
            ::new (static_cast<void*>(to)) _Block<F>*(block);
        },
        [](unsigned char* from, unsigned char* to) noexcept {
            // Le bloc change seulement de propriétaire : la function déplacée le partage encore,
            // elle garde donc sa référence ; la nouvelle en prend une.
            auto* block = _block<F>(from);
            block->header.references.fetch_add(1, std::memory_order_relaxed);
            ::new (static_cast<void*>(to)) _Block<F>*(block);
        },
        [](unsigned char* storage) noexcept {
            auto* block = _block<F>(storage);
            if (block->header.references.fetch_sub(1, std::memory_order_acq_rel) != 1) return;
            const auto* allocator = block->header.allocator;
            block->~_Block<F>();
            release_block(*allocator, block);
        },
        true,
    };

    alignas(void*) unsigned char _storage[_inline_size];
    const _Operations* _operations;
};

}  // namespace rgl::standard
