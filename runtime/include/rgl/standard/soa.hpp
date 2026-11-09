#pragma once

// soa_array<Layout> : un tableau de molds rangé en colonnes (Structure of Arrays). Chaque champ du
// mold, sous-molds aplatis, a sa propre colonne, un array<T> ; un élément n'existe pas en mémoire
// d'un seul tenant : operator[] le reconstruit (gather), push le répartit dans les colonnes
// (scatter). Une boucle qui ne lit que quelques champs ne charge ainsi que leurs colonnes, une fois
// l'appel inliné par Clang.
//
// Le compilateur RGL génère un Layout par mold :
//
//     struct rgl_l_m_Entity {
//         using value_type = rgl_m_Entity;
//         struct columns { array<float> rgl_k0; array<float> rgl_k1; array<double> rgl_k2; };
//         static value_type gather(const columns&, std::size_t);
//         static void scatter(columns&, const value_type&);
//         static void store(columns&, std::size_t, const value_type&);
//         static void reserve(columns&, std::size_t);
//     };
//
// Il se lit comme les autres tableaux (has_index, operator[], at, size, rgl_sequence) et se
// convertit de même (fits, from, try_from), mais operator[] rend une valeur, pas une référence.
// Une variable mut le modifie élément par élément (set), ou champ par champ dans sa colonne
// (columns(), à l'index vérifié par index()).

#include <concepts>
#include <cstddef>
#include <initializer_list>
#include <utility>

#include "rgl/standard/array.hpp"
#include "rgl/standard/nullable.hpp"

namespace rgl::standard {

template <class Layout>
class soa_array {
   public:
    using value_type = typename Layout::value_type;

    // Marque des tableaux : format.hpp les écrit en texte, spread() les accepte.
    using rgl_sequence = value_type;

    soa_array() = default;

    // Le littéral [a, b, c].
    soa_array(std::initializer_list<value_type> items) {
        reserve(items.size());
        for (const value_type& item : items) push(item);
    }

    std::size_t size() const noexcept { return size_; }
    bool empty() const noexcept { return size_ == 0; }

    // Réserve la place de capacity éléments dans chaque colonne.
    void reserve(std::size_t capacity) { Layout::reserve(columns_, capacity); }

    template <std::integral I>
    bool has_index(I index) const noexcept {
        return detail::index_fits(index, size_);
    }

    // L'élément index, reconstruit depuis les colonnes, sans contrôle.
    value_type operator[](std::size_t index) const { return Layout::gather(columns_, index); }

    // L'élément index, ou l'arrêt du programme hors limites, avec l'endroit du source RGL.
    template <std::integral I>
    value_type at(I index, const char* where) const {
        if (!has_index(index)) detail::index_out_of_range(index, size_, where);
        return Layout::gather(columns_, detail::resolve_index(index, size_));
    }

    // L'élément index, ou null hors limites.
    template <std::integral I>
    nullable<value_type> try_at(I index) const {
        if (!has_index(index)) return std::nullopt;
        return Layout::gather(columns_, detail::resolve_index(index, size_));
    }

    // L'index, vérifié : l'arrêt du programme hors limites, avec l'endroit du source RGL.
    template <std::integral I>
    std::size_t index(I index, const char* where) const noexcept {
        if (!has_index(index)) detail::index_out_of_range(index, size_, where);
        return detail::resolve_index(index, size_);
    }

    // Remplace l'élément index (déjà vérifié) : chaque champ dans sa colonne.
    void set(std::size_t index, const value_type& value) { Layout::store(columns_, index, value); }

    // Les colonnes, pour modifier un champ de l'élément index (déjà vérifié) sans reconstruire l'élément.
    typename Layout::columns& columns() noexcept { return columns_; }

    // Ajoute un élément à la fin : chaque champ dans sa colonne. La valeur peut venir du tableau lui-même.

    void push(const value_type& value) {
        Layout::scatter(columns_, value);
        ++size_;
    }

    // Ajoute à la fin tous les éléments de items (le spread [...a, …]) ; items peut être *this.
    template <class Items>
        requires requires { typename Items::rgl_sequence; }
    void append(const Items& items) {
        const std::size_t count = items.size();
        reserve(size_ + count);
        for (std::size_t i = 0; i < count; ++i) push(items[i]);
    }

    // Un tableau en colonnes prend n'importe quel nombre d'éléments.
    static constexpr bool fits(std::size_t) noexcept { return true; }

    template <class Items>
        requires requires { typename Items::rgl_sequence; }
    static soa_array from(const Items& items, const char* = "") {
        soa_array result;
        result.append(items);
        return result;
    }

    template <class Items>
        requires requires { typename Items::rgl_sequence; }
    static nullable<soa_array> try_from(const Items& items) {
        return from(items);
    }

    // Parcours en lecture : chaque élément est reconstruit à la volée.
    class iterator {
       public:
        iterator(const soa_array* owner, std::size_t index) noexcept : owner_(owner), index_(index) {}
        value_type operator*() const { return (*owner_)[index_]; }
        iterator& operator++() noexcept {
            ++index_;
            return *this;
        }
        bool operator==(const iterator& other) const noexcept { return index_ == other.index_; }

       private:
        const soa_array* owner_;
        std::size_t index_;
    };

    iterator begin() const noexcept { return iterator(this, 0); }
    iterator end() const noexcept { return iterator(this, size_); }

    // Deux tableaux sont égaux s'ils ont les mêmes éléments, dans le même ordre.
    friend bool operator==(const soa_array& left, const soa_array& right) {
        if (left.size_ != right.size_) return false;
        for (std::size_t i = 0; i < left.size_; ++i) {
            if (!(left[i] == right[i])) return false;
        }
        return true;
    }

   private:
    typename Layout::columns columns_{};
    std::size_t size_ = 0;
};

// soa_fixed_array<Layout, N> : un [mold;N] rangé en colonnes de N éléments, rangées dans l'objet (aucune allocation) : un bloc
// d'AoSoA, et un tableau [[mold;N]] est un array de tels blocs. Il se lit comme un fixed_array (has_index, operator[], at, size,
// rgl_sequence, fits, from, try_from) ; operator[] rend une valeur reconstruite depuis les colonnes. Le Layout fournit en plus
// `fixed_columns<N>` (une colonne `T rgl_kI[N]` par champ) ; gather et store lisent et écrivent n'importe quelles colonnes.
template <class Layout, std::size_t N>
class soa_fixed_array {
    static_assert(N > 0, "un tableau [T;N] a au moins un élément");

   public:
    using value_type = typename Layout::value_type;
    using rgl_sequence = value_type;

    // Le littéral [a, b, c] : exactement N éléments.
    template <class... U>
        requires(sizeof...(U) == N && (std::convertible_to<U, value_type> && ...))
    constexpr soa_fixed_array(U&&... items) noexcept {
        std::size_t position = 0;
        ((Layout::store(columns_, position++, static_cast<value_type>(static_cast<U&&>(items)))), ...);
    }

    static constexpr std::size_t size() noexcept { return N; }

    template <std::integral I>
    constexpr bool has_index(I index) const noexcept {
        return detail::index_fits(index, N);
    }

    constexpr value_type operator[](std::size_t index) const { return Layout::gather(columns_, index); }

    template <std::integral I>
    value_type at(I index, const char* where) const {
        if (!has_index(index)) detail::index_out_of_range(index, N, where);
        return Layout::gather(columns_, detail::resolve_index(index, N));
    }

    template <std::integral I>
    nullable<value_type> try_at(I index) const {
        if (!has_index(index)) return std::nullopt;
        return Layout::gather(columns_, detail::resolve_index(index, N));
    }

    template <std::integral I>
    std::size_t index(I index, const char* where) const noexcept {
        if (!has_index(index)) detail::index_out_of_range(index, N, where);
        return detail::resolve_index(index, N);
    }

    void set(std::size_t index, const value_type& value) { Layout::store(columns_, index, value); }

    typename Layout::template fixed_columns<N>& columns() noexcept { return columns_; }

    static constexpr bool fits(std::size_t size) noexcept { return size == N; }

    template <class Items>
        requires requires { typename Items::rgl_sequence; }
    static soa_fixed_array from(const Items& items, const char* where) noexcept {
        if (!fits(items.size())) detail::size_out_of_bounds("tableau de taille différente d'un tableau [T;N]", items.size(), where);
        return _copy(items, std::make_index_sequence<N>{});
    }

    template <class Items>
        requires requires { typename Items::rgl_sequence; }
    static nullable<soa_fixed_array> try_from(const Items& items) noexcept {
        if (!fits(items.size())) return std::nullopt;
        return _copy(items, std::make_index_sequence<N>{});
    }

    class iterator {
       public:
        constexpr iterator(const soa_fixed_array* owner, std::size_t index) noexcept : owner_(owner), index_(index) {}
        constexpr value_type operator*() const { return (*owner_)[index_]; }
        constexpr iterator& operator++() noexcept {
            ++index_;
            return *this;
        }
        constexpr bool operator==(const iterator& other) const noexcept { return index_ == other.index_; }

       private:
        const soa_fixed_array* owner_;
        std::size_t index_;
    };

    constexpr iterator begin() const noexcept { return iterator(this, 0); }
    constexpr iterator end() const noexcept { return iterator(this, N); }

    friend constexpr bool operator==(const soa_fixed_array& left, const soa_fixed_array& right) {
        for (std::size_t i = 0; i < N; ++i) {
            if (!(left[i] == right[i])) return false;
        }
        return true;
    }

   private:
    typename Layout::template fixed_columns<N> columns_{};

    template <class Items, std::size_t... I>
    static soa_fixed_array _copy(const Items& items, std::index_sequence<I...>) noexcept {
        return soa_fixed_array(static_cast<value_type>(items[I])...);
    }
};

namespace detail {

template <class Layout, class Part>
void add_soa_part(soa_array<Layout>& result, const Part& part) {
    result.push(typename Layout::value_type(part));
}

template <class Layout, class Items>
void add_soa_part(soa_array<Layout>& result, const spread_part<Items>& part) {
    result.append(part.items);
}

}  // namespace detail

// Un nouveau tableau en colonnes fait de ses morceaux, comme make_array : [...a, x] devient
// make_soa<Layout>(spread(a), x). La taille est calculée d'abord.
template <class Layout, class... Parts>
soa_array<Layout> make_soa(const Parts&... parts) {
    std::size_t total = 0;
    ((total += detail::part_size<typename Layout::value_type>(parts)), ...);
    soa_array<Layout> result;
    result.reserve(total);
    (detail::add_soa_part<Layout>(result, parts), ...);
    return result;
}

}  // namespace rgl::standard
