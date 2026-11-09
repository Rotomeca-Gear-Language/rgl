#pragma once

// Les tuples de RGL, (int, string) : une valeur de plusieurs éléments, lus par position (Item1,
// Item2… ou leur nom, comme en C#). Pour l'instant un alias de std::tuple : le code généré n'écrit
// que rgl::standard::tuple et rgl::standard::item, pour que le type puisse changer sans rien
// toucher d'autre. Un tuple de valeurs connues est constexpr.

#include <cstddef>
#include <tuple>

namespace rgl::standard {

template <class... T>
using tuple = std::tuple<T...>;

// L'élément I (à partir de 0) d'un tuple : Item1 est item<0>.
template <std::size_t I, class... T>
constexpr const auto& item(const tuple<T...>& value) noexcept {
    return std::get<I>(value);
}

// L'élément I, modifiable : le contenu d'une variable mut.
template <std::size_t I, class... T>
constexpr auto& item(tuple<T...>& value) noexcept {
    return std::get<I>(value);
}

}  // namespace rgl::standard
