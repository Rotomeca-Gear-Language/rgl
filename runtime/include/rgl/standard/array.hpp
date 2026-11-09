#pragma once

// Les tableaux de RGL. Un tableau grandit seulement par la fin (push), et un nouveau tableau se
// construit par spread ([...a, x]). Ses éléments ne se modifient en place que par une variable mut :
// le code généré passe alors par les accès non const (operator[], at).
//
// - array<T>                    : [T], dynamique, sur le tas (AbiAllocator, croissance ×1,5) ;
// - fixed_array<T, N>           : [T;N], N éléments dans l'objet, utilisable en constexpr ;
// - bounded_array<T, Min, Max>  : [T;N..M], de Min à Max éléments, dans l'objet, sans allocation ;
// - non_empty_array<T>          : [T+], un array<T> qui a toujours au moins un élément.
//
// Lecture par index, la même pour les quatre : has_index(i) puis operator[] (non contrôlé),
// at(i, where) qui arrête le programme hors limites, ou try_at(i) qui donne nullptr. Dans un guard,
// le code généré teste has_index et fait échouer le guard au lieu d'arrêter le programme.
//
// Passage d'une sorte à l'autre, le même pour les quatre : fits(taille), from(a, where) qui arrête le
// programme si les éléments de a ne tiennent pas, try_from(a) qui donne null. La source peut être
// n'importe quel tableau marqué rgl_sequence, un soa_array compris (soa.hpp) : elle n'est lue que
// par size() et operator[], jamais par data().
//
// Copier un tableau recopie ses éléments (c'est une valeur RGL) ; le déplacer ne coûte rien.
// Rien ne lève d'exception : une mémoire épuisée ou une taille hors des bornes arrête le programme.

#include <concepts>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <initializer_list>
#include <new>
#include <type_traits>
#include <utility>

#include "rgl/standard/memory.hpp"
#include "rgl/standard/nullable.hpp"

namespace rgl::standard {

namespace detail {

// Volontairement non constexpr : un index hors limites pendant une évaluation à la compilation est
// une erreur de compilation. Le compilateur RGL vérifie lui-même les index connus.
[[noreturn]] inline void index_out_of_range(long long index, std::size_t size, const char* where) noexcept {
    std::fprintf(stderr, "rgl : index %lld hors limites pour un tableau de %zu élément(s) (%s)\n", index, size, where);
    std::abort();
}

[[noreturn]] inline void index_out_of_range(unsigned long long index, std::size_t size, const char* where) noexcept {
    std::fprintf(stderr, "rgl : index %llu hors limites pour un tableau de %zu élément(s) (%s)\n", index, size, where);
    std::abort();
}

// Un tableau qui ne respecte pas sa taille : borné hors de ses bornes, non vide qui serait vide.
[[noreturn]] inline void size_out_of_bounds(const char* what, std::size_t size, const char* where) noexcept {
    std::fprintf(stderr, "rgl : %s (%zu élément(s)) (%s)\n", what, size, where);
    std::abort();
}

// Indique si index désigne un élément d'un tableau de size éléments : un index positif compte depuis
// le début, un index négatif depuis la fin (-1 est le dernier).
template <std::integral I>
constexpr bool index_fits(I index, std::size_t size) noexcept {
    if constexpr (std::is_signed_v<I>) {
        if (index < 0) {
            // -(index + 1) évite de négativer le plus petit entier.
            return static_cast<unsigned long long>(-(index + 1)) < size;
        }
    }
    return static_cast<unsigned long long>(index) < size;
}

// Le rang depuis le début de l'élément désigné par index, déjà vérifié par index_fits.
template <std::integral I>
constexpr std::size_t resolve_index(I index, std::size_t size) noexcept {
    if constexpr (std::is_signed_v<I>) {
        if (index < 0) return size - 1 - static_cast<std::size_t>(-(index + 1));
    }
    return static_cast<std::size_t>(index);
}

template <std::integral I>
[[noreturn]] void index_out_of_range(I index, std::size_t size, const char* where) noexcept {
    if constexpr (std::is_signed_v<I>) index_out_of_range(static_cast<long long>(index), size, where);
    else index_out_of_range(static_cast<unsigned long long>(index), size, where);
}

// La lecture commune aux quatre tableaux, et l'accès modifiable à un élément (variable mut). Derived
// fournit data() (const et non const) et size().
template <class Derived, class T>
class readable {
   public:
    // Marque des tableaux : format.hpp les écrit en texte, spread() les accepte.
    using rgl_sequence = T;

    template <std::integral I>
    constexpr bool has_index(I index) const noexcept {
        return index_fits(index, _self().size());
    }

    // L'élément index, sans contrôle : seulement après has_index, ou pour un index déjà vérifié.
    constexpr const T& operator[](std::size_t index) const noexcept { return _self().data()[index]; }

    // L'élément index, ou l'arrêt du programme hors limites, avec l'endroit du source RGL.
    template <std::integral I>
    constexpr const T& at(I index, const char* where) const noexcept {
        if (!has_index(index)) index_out_of_range(index, _self().size(), where);
        return _self().data()[detail::resolve_index(index, _self().size())];
    }

    // L'élément index, modifiable, sans contrôle : seulement après has_index, ou pour un index déjà vérifié.
    constexpr T& operator[](std::size_t index) noexcept { return _self_mutable().data()[index]; }

    // L'élément index, modifiable, ou l'arrêt du programme hors limites.
    template <std::integral I>
    constexpr T& at(I index, const char* where) noexcept {
        if (!has_index(index)) index_out_of_range(index, _self().size(), where);
        return _self_mutable().data()[detail::resolve_index(index, _self().size())];
    }

    // L'élément index, ou nullptr hors limites.
    template <std::integral I>
    constexpr const T* try_at(I index) const noexcept {
        return has_index(index) ? _self().data() + detail::resolve_index(index, _self().size()) : nullptr;
    }

    constexpr bool empty() const noexcept { return _self().size() == 0; }
    constexpr const T* begin() const noexcept { return _self().data(); }
    constexpr const T* end() const noexcept { return _self().data() + _self().size(); }

    // Deux tableaux du même type sont égaux s'ils ont les mêmes éléments, dans le même ordre.
    friend constexpr bool operator==(const Derived& left, const Derived& right) noexcept {
        if (left.size() != right.size()) return false;
        for (std::size_t i = 0; i < left.size(); ++i) {
            if (!(left.data()[i] == right.data()[i])) return false;
        }
        return true;
    }

   private:
    constexpr const Derived& _self() const noexcept { return static_cast<const Derived&>(*this); }
    constexpr Derived& _self_mutable() noexcept { return static_cast<Derived&>(*this); }
};

}  // namespace detail

// [T] : tableau dynamique. Les éléments vivent dans un bloc du tas, obtenu par un AbiAllocator
// (celui par défaut, ou celui donné à la construction, gardé par les copies).
template <class T>
class array : public detail::readable<array<T>, T> {
   public:
    array() noexcept = default;

    // Tableau vide qui allouera avec allocator : il doit vivre plus longtemps que le tableau.
    explicit array(const AbiAllocator& allocator) noexcept : alloc_(&allocator) {}

    // Tableau de ces éléments, dans l'ordre : le littéral [1, 2, 3].
    array(std::initializer_list<T> items) {
        reserve(items.size());
        _construct_each([&](array& self) { for (const T& item : items) self._construct(item); });
    }

    // Une copie garde l'allocateur de l'original (un pool, ou le système). Celui d'une arène est gardé seulement tant que cette arène est
    // celle où l'on crée (l'allocateur ambiant) : sa mémoire est rendue à sa remise à zéro, et la copie, faite ailleurs, peut lui survivre.
    // Elle est alors sur le tas.
    array(const array& other) : alloc_(_outliving(other.alloc_)) {
        counters::note_copy(counters::copy_kind::array, other.size_);
        reserve(other.size_);
        _construct_each([&](array& self) { for (const T& item : other) self._construct(item); });
    }

    // Un déplacement reprend le bloc de l'original, sauf pour une arène qui n'est pas celle où l'on crée (voir la copie) : les éléments
    // passent alors, un à un, dans un bloc du tas, et l'original est vidé.
    array(array&& other) noexcept {
        if (other.alloc_->scoped && other.alloc_ != &default_allocator()) _move_elements_in(other);
        else _steal(other);
    }

    // Une affectation garde l'allocateur de ce tableau quand la source est dans une arène qui n'est pas la sienne : ce tableau peut
    // avoir été créé hors de l'arène et lui survivre, il ne prend jamais son bloc.
    array& operator=(const array& other) {
        if (this == &other) return *this;
        if (other.alloc_->scoped && other.alloc_ != alloc_) {
            counters::note_copy(counters::copy_kind::array, other.size_);
            _destroy();
            reserve(other.size_);
            _construct_each([&](array& self) { for (const T& item : other) self._construct(item); });
            return *this;
        }
        array copy(other);
        _destroy();
        _steal(copy);
        return *this;
    }

    array& operator=(array&& other) noexcept {
        if (this == &other) return *this;
        if (other.alloc_->scoped && other.alloc_ != alloc_) {
            _destroy();
            _move_elements_in(other);
            return *this;
        }
        _destroy();
        _steal(other);
        return *this;
    }

    ~array() { _destroy(); }

    const T* data() const noexcept { return data_; }
    T* data() noexcept { return data_; }
    std::size_t size() const noexcept { return size_; }
    std::size_t capacity() const noexcept { return capacity_; }

    // L'allocateur de ce tableau : celui qu'il garde toute sa vie.
    const AbiAllocator& allocator() const noexcept { return *alloc_; }

    // Un [T] prend n'importe quel nombre d'éléments : les éléments de items, recopiés.
    static constexpr bool fits(std::size_t) noexcept { return true; }

    template <class Items>
        requires requires { typename Items::rgl_sequence; }
    static array from(const Items& items, const char* = "") {
        array result;
        result.append(items);
        return result;
    }

    template <class Items>
        requires requires { typename Items::rgl_sequence; }
    static nullable<array> try_from(const Items& items) {
        return from(items);
    }

    // Réserve la place de capacity éléments au moins : les ajouts qui suivent n'allouent plus.
    void reserve(std::size_t capacity) {
        if (capacity > capacity_) _reallocate(capacity);
    }

    // Ajoute un élément à la fin. Le compilateur RGL ne le permet que sur un tableau déclaré
    // localement, jamais sur un tableau reçu en paramètre. La valeur peut venir du tableau lui-même.
    void push(const T& value) {
        if (size_ < capacity_) {
            _construct_one(value);
            return;
        }
        T copy(value);   // value peut pointer dans le bloc que la croissance va libérer
        _grow();
        _construct_one(static_cast<T&&>(copy));
    }

    void push(T&& value) {
        if (size_ == capacity_) {
            T moved(static_cast<T&&>(value));
            _grow();
            _construct_one(static_cast<T&&>(moved));
            return;
        }
        _construct_one(static_cast<T&&>(value));
    }

    // Ajoute un élément à la fin d'un tableau dont la place est déjà réservée (reserve) : jamais de croissance, donc aucun test ni
    // appel dans la boucle qui remplit le tableau, que Clang peut alors vectoriser.
    void push_reserved(T&& value) { _construct_one(static_cast<T&&>(value)); }

    // Ajoute à la fin tous les éléments de items (le spread [...a, …]) ; items peut être *this.
    template <class Items>
        requires requires { typename Items::rgl_sequence; }
    void append(const Items& items) {
        const std::size_t count = items.size();
        reserve(size_ + count);
        // Après reserve, items[i] lit le bloc à jour même si items est *this.
        _construct_each([&](array& self) { for (std::size_t i = 0; i < count; ++i) self._construct(items[i]); });
    }

   private:
    T* data_ = nullptr;
    std::size_t size_ = 0;
    std::size_t capacity_ = 0;
    // L'allocateur du tableau est fixé à sa construction : celui du pool ou de l'arène ouvert à cet instant, sinon le système.
    const AbiAllocator* alloc_ = &default_allocator();

    const AbiAllocator& _allocator() const noexcept { return *alloc_; }

    // L'allocateur que garde la copie d'un objet alloué par source : le même, sauf une arène.
    static const AbiAllocator* _outliving(const AbiAllocator* source) noexcept { return source->scoped && source != &default_allocator() ? &heap_allocator() : source; }

    // Les éléments se construisent avec l'allocateur de ce tableau pour allocateur ambiant : un texte ou un tableau qui vient d'une arène
    // n'est repris tel quel que par un tableau de cette arène, et recopié sur le tas par un autre. Un élément sans mémoire n'en a pas besoin.
    template <class Body>
    void _construct_each(Body&& body) {
        if constexpr (std::is_trivially_copyable_v<T>) {
            body(*this);
        } else {
            allocator_scope scope(*alloc_);
            body(*this);
        }
    }

    // Une valeur construite dans ce tableau (voir _construct_each).
    template <class V>
    void _construct_one(V&& value) {
        if constexpr (std::is_trivially_copyable_v<T>) {
            _construct(static_cast<V&&>(value));
        } else {
            allocator_scope scope(*alloc_);
            _construct(static_cast<V&&>(value));
        }
    }

    // Reprend les éléments de other, dont l'arène n'est pas celle de ce tableau : ils passent dans un bloc de ce tableau (celui du système, ou
    // l'allocateur où ce tableau a été créé) ; other est vidé.
    void _move_elements_in(array& other) noexcept {
        reserve(other.size_);
        _construct_each([&](array& self) { for (std::size_t i = 0; i < other.size_; ++i) self._construct(static_cast<T&&>(other.data_[i])); });
        other._destroy();
    }

    template <class V>
    void _construct(V&& value) {
        ::new (static_cast<void*>(data_ + size_)) T(static_cast<V&&>(value));
        ++size_;
    }

    // Croissance ×1,5, au moins 4 éléments.
    void _grow() {
        const std::size_t wanted = capacity_ < 4 ? 4 : capacity_ + capacity_ / 2;
        _reallocate(wanted < capacity_ ? capacity_ + 1 : wanted);
    }

    void _reallocate(std::size_t capacity) {
        if (capacity > SIZE_MAX / sizeof(T)) out_of_memory(SIZE_MAX);
        const std::size_t bytes = capacity * sizeof(T);

        if constexpr (std::is_trivially_copyable_v<T>) {
            data_ = static_cast<T*>(data_ ? reallocate_or_abort(_allocator(), data_, bytes) : allocate_or_abort(_allocator(), bytes));
        } else {
            // Un élément non trivial ne se déplace pas octet par octet : nouveau bloc, éléments
            // déplacés un à un, ancien bloc libéré.
            T* block = static_cast<T*>(allocate_or_abort(_allocator(), bytes));
            allocator_scope scope(*alloc_);
            for (std::size_t i = 0; i < size_; ++i) {
                ::new (static_cast<void*>(block + i)) T(static_cast<T&&>(data_[i]));
                data_[i].~T();
            }
            if (data_) release_block(_allocator(), data_);
            data_ = block;
        }
        capacity_ = capacity;
    }

    void _destroy() noexcept {
        if constexpr (!std::is_trivially_destructible_v<T>) {
            for (std::size_t i = 0; i < size_; ++i) data_[i].~T();
        }
        if (data_) release_block(_allocator(), data_);
        data_ = nullptr;
        size_ = capacity_ = 0;
    }

    void _steal(array& other) noexcept {
        data_ = std::exchange(other.data_, nullptr);
        size_ = std::exchange(other.size_, 0);
        capacity_ = std::exchange(other.capacity_, 0);
        alloc_ = other.alloc_;
    }
};

// [T;N] : exactement N éléments, rangés dans l'objet. Constructible en constexpr : un littéral
// [T;N] connu à la compilation devient une constante.
template <class T, std::size_t N>
class fixed_array : public detail::readable<fixed_array<T, N>, T> {
    static_assert(N > 0, "un tableau [T;N] a au moins un élément");

   public:
    template <class... U>
        requires(sizeof...(U) == N)
    constexpr fixed_array(U&&... items) : items_{static_cast<T>(static_cast<U&&>(items))...} {}

    constexpr const T* data() const noexcept { return items_; }
    constexpr T* data() noexcept { return items_; }
    static constexpr std::size_t size() noexcept { return N; }

    // Indique si un tableau de size éléments en fait un [T;N] : exactement N.
    static constexpr bool fits(std::size_t size) noexcept { return size == N; }

    // Le [T;N] des éléments de items, ou l'arrêt du programme s'ils ne sont pas exactement N.
    template <class Items>
        requires requires { typename Items::rgl_sequence; }
    static fixed_array from(const Items& items, const char* where) noexcept {
        if (!fits(items.size())) detail::size_out_of_bounds("tableau de taille différente d'un tableau [T;N]", items.size(), where);
        return _copy(items, std::make_index_sequence<N>{});
    }

    // Le [T;N] des éléments de items, ou null s'ils ne sont pas exactement N (as?).
    template <class Items>
        requires requires { typename Items::rgl_sequence; }
    static nullable<fixed_array> try_from(const Items& items) noexcept {
        if (!fits(items.size())) return std::nullopt;
        return _copy(items, std::make_index_sequence<N>{});
    }

   private:
    T items_[N];

    template <class Items, std::size_t... I>
    static fixed_array _copy(const Items& items, std::index_sequence<I...>) noexcept {
        return fixed_array(items[I]...);
    }
};

// [T;N..M] : de Min à Max éléments, rangés dans l'objet (aucune allocation). Après sa construction,
// sa taille ne fait que grandir par push, sans dépasser Max. Attention : l'objet occupe la place de
// Max éléments, même vide.
template <class T, std::size_t Min, std::size_t Max>
class bounded_array : public detail::readable<bounded_array<T, Min, Max>, T> {
    static_assert(Min <= Max, "les bornes d'un tableau [T;N..M] vérifient N <= M");
    static_assert(Max > 0, "un tableau [T;N..M] peut contenir au moins un élément");

   public:
    // Tableau vide : seulement quand Min vaut 0.
    bounded_array() noexcept
        requires(Min == 0)
    = default;

    // Le littéral [a, b, c] : son nombre d'éléments est entre Min et Max (le compilateur RGL l'a
    // vérifié ; sinon, le programme s'arrête).
    bounded_array(std::initializer_list<T> items) noexcept {
        if (!fits(items.size())) detail::size_out_of_bounds("littéral hors des bornes d'un tableau borné", items.size(), "");
        for (const T& item : items) _construct(item);
    }

    bounded_array(const bounded_array& other) : detail::readable<bounded_array, T>() {
        for (const T& item : other) _construct(item);
    }

    bounded_array(bounded_array&& other) noexcept : detail::readable<bounded_array, T>() {
        for (std::size_t i = 0; i < other.size_; ++i) _construct(static_cast<T&&>(other._items()[i]));
    }

    bounded_array& operator=(const bounded_array& other) {
        if (this == &other) return *this;
        _destroy();
        for (const T& item : other) _construct(item);
        return *this;
    }

    bounded_array& operator=(bounded_array&& other) noexcept {
        if (this == &other) return *this;
        _destroy();
        for (std::size_t i = 0; i < other.size_; ++i) _construct(static_cast<T&&>(other._items()[i]));
        return *this;
    }

    ~bounded_array() { _destroy(); }

    // Indique si un tableau de size éléments tient dans les bornes.
    static constexpr bool fits(std::size_t size) noexcept { return size >= Min && size <= Max; }

    // Le tableau borné des éléments de items, ou l'arrêt du programme s'ils sont trop ou trop peu.
    template <class Items>
        requires requires { typename Items::rgl_sequence; }
    static bounded_array from(const Items& items, const char* where) noexcept {
        if (!fits(items.size())) detail::size_out_of_bounds("tableau hors des bornes d'un tableau borné", items.size(), where);
        return bounded_array(items);
    }

    // Le tableau borné des éléments de items, ou null s'ils sont trop ou trop peu (as?).
    template <class Items>
        requires requires { typename Items::rgl_sequence; }
    static nullable<bounded_array> try_from(const Items& items) noexcept {
        if (!fits(items.size())) return std::nullopt;
        return bounded_array(items);
    }

    const T* data() const noexcept { return _items(); }
    T* data() noexcept { return _items(); }
    std::size_t size() const noexcept { return size_; }
    static constexpr std::size_t capacity() noexcept { return Max; }

    // Indique s'il reste de la place pour un élément.
    bool can_push() const noexcept { return size_ < Max; }

    // Ajoute un élément à la fin, ou arrête le programme si le tableau est plein.
    void push(const T& value, const char* where) noexcept {
        if (!can_push()) detail::size_out_of_bounds("ajout dans un tableau borné déjà plein", size_, where);
        _construct(value);
    }

   private:
    alignas(T) unsigned char storage_[sizeof(T) * Max];
    std::size_t size_ = 0;

    template <class Items>
    explicit bounded_array(const Items& items) noexcept {
        for (const T& item : items) _construct(item);
    }

    T* _items() noexcept { return std::launder(reinterpret_cast<T*>(storage_)); }
    const T* _items() const noexcept { return std::launder(reinterpret_cast<const T*>(storage_)); }

    template <class V>
    void _construct(V&& value) {
        ::new (static_cast<void*>(reinterpret_cast<T*>(storage_) + size_)) T(static_cast<V&&>(value));
        ++size_;
    }

    void _destroy() noexcept {
        if constexpr (!std::is_trivially_destructible_v<T>) {
            for (std::size_t i = 0; i < size_; ++i) _items()[i].~T();
        }
        size_ = 0;
    }
};

// [T+] : un tableau dynamique qui a toujours au moins un élément ; first() et last() sont donc
// toujours valides. Il se lit comme un [T] (items()).
template <class T>
class non_empty_array : public detail::readable<non_empty_array<T>, T> {
   public:
    // Le littéral [a, b] : au moins un élément (le compilateur RGL l'a vérifié ; sinon, le
    // programme s'arrête).
    non_empty_array(std::initializer_list<T> items) : items_(items) {
        if (items_.empty()) detail::size_out_of_bounds("littéral vide pour un tableau [T+]", 0, "");
    }

    // Indique si un tableau de size éléments en fait un [T+] : au moins un.
    static constexpr bool fits(std::size_t size) noexcept { return size > 0; }

    // Le tableau non vide des éléments de items, ou l'arrêt du programme s'il est vide.
    template <class Items>
        requires requires { typename Items::rgl_sequence; }
    static non_empty_array from(const Items& items, const char* where) noexcept {
        if (!fits(items.size())) detail::size_out_of_bounds("tableau vide là où un tableau [T+] est attendu", 0, where);
        return non_empty_array(array<T>::from(items));
    }

    // Le tableau non vide des éléments de items, ou null s'il est vide (as?).
    template <class Items>
        requires requires { typename Items::rgl_sequence; }
    static nullable<non_empty_array> try_from(const Items& items) noexcept {
        if (!fits(items.size())) return std::nullopt;
        return non_empty_array(array<T>::from(items));
    }

    const T* data() const noexcept { return items_.data(); }
    T* data() noexcept { return items_.data(); }
    std::size_t size() const noexcept { return items_.size(); }

    const T& first() const noexcept { return items_[0]; }
    const T& last() const noexcept { return items_[items_.size() - 1]; }

    // Les mêmes éléments, vus comme un [T].
    const array<T>& items() const noexcept { return items_; }

    void push(const T& value) { items_.push(value); }
    void push(T&& value) { items_.push(static_cast<T&&>(value)); }

   private:
    array<T> items_;

    explicit non_empty_array(array<T>&& items) noexcept : items_(static_cast<array<T>&&>(items)) {}
};

// Un tableau étalé dans un nouveau tableau : le ...a de [...a, x].
template <class Items>
struct spread_part {
    const Items& items;
};

template <class Items>
    requires requires { typename Items::rgl_sequence; }
constexpr spread_part<Items> spread(const Items& items) noexcept {
    return spread_part<Items>{items};
}

namespace detail {

template <class T, class Part>
constexpr std::size_t part_size(const Part&) noexcept {
    return 1;
}

template <class T, class Items>
constexpr std::size_t part_size(const spread_part<Items>& part) noexcept {
    return part.items.size();
}

template <class T, class Part>
void add_part(array<T>& result, const Part& part) {
    result.push(T(part));
}

template <class T, class Items>
void add_part(array<T>& result, const spread_part<Items>& part) {
    result.append(part.items);
}

}  // namespace detail

// Un nouveau [T] fait de ses morceaux, dans l'ordre : chaque morceau est une valeur, ou spread(a)
// pour tous les éléments d'un tableau. [...a, x, ...b] devient make_array<T>(spread(a), x, spread(b)).
// La taille est calculée d'abord : une seule allocation.
template <class T, class... Parts>
array<T> make_array(const Parts&... parts) {
    std::size_t total = 0;
    ((total += detail::part_size<T>(parts)), ...);
    array<T> result;
    result.reserve(total);
    (detail::add_part<T>(result, parts), ...);
    return result;
}

// [...a, x, …] quand a n'est plus lu ensuite : le tableau a est repris tel quel (son bloc, ses éléments) et les autres morceaux s'ajoutent à sa suite,
// avec la croissance ordinaire (×1,5) : une boucle qui ajoute un élément à chaque tour est linéaire, et non plus quadratique. Les autres morceaux
// ont déjà été évalués avant l'appel, et le compilateur n'écrit cette forme que si aucun d'eux ne lit a. Si first n'est pas une rvalue de
// array<T> (une constante, un temporaire constant), c'est make_array ordinaire.
template <class T, class First, class... Rest>
array<T> make_array_from(First&& first, const Rest&... rest) {
    if constexpr (std::is_same_v<First, array<T>>) {
        std::size_t extra = 0;
        ((extra += detail::part_size<T>(rest)), ...);
        array<T> result(static_cast<array<T>&&>(first));
        const std::size_t needed = result.size() + extra;
        if (needed > result.capacity()) {
            const std::size_t grown = result.capacity() + result.capacity() / 2;
            result.reserve(needed > grown ? needed : grown);
        }
        (detail::add_part<T>(result, rest), ...);
        return result;
    } else {
        return make_array<T>(spread(first), rest...);
    }
}

}  // namespace rgl::standard
