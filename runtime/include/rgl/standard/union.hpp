#pragma once

// Les unions et les résultats de RGL. `int | string` est rgl::standard::union_of<…>, pour l'instant
// un alias de std::variant (une union taguée maison viendra avec les optimisations) ; `T ! E` est
// rgl::standard::result<T, E>, un succès ou une erreur avec une étiquette, même si T et E sont le
// même type. Le code généré n'écrit que ces noms. Tout est dans l'en-tête : un programme sans
// union ni résultat ne l'inclut pas. Rien ne lève d'exception : la valeur lue est toujours celle
// que le code généré vient de tester.

#include <cstddef>
#include <type_traits>
#include <utility>
#include <variant>

namespace rgl::standard {

template <class... T>
using union_of = std::variant<T...>;

// Le membre I d'une union, pour la construire : union_of<int, string>(member<0>, 42).
template <std::size_t I>
inline constexpr std::in_place_index_t<I> member{};

// Le membre I d'une union dont le code a vérifié l'index.
template <std::size_t I, class... T>
const auto& get(const union_of<T...>& value) noexcept {
    return *std::get_if<I>(&value);
}

// Une union mise dans une union plus large, qui contient tous ses membres.
template <class To, class... T>
To widen(const union_of<T...>& value) {
    return std::visit([](const auto& inner) { return To(std::in_place_type<std::decay_t<decltype(inner)>>, inner); }, value);
}

// Une union réduite aux membres qui restent : le bras « default x » d'un match. Un membre absent de
// To ne peut pas se présenter (les autres bras l'ont pris).
template <class T, class V>
struct in_union;

template <class T, class... U>
struct in_union<T, union_of<U...>> : std::bool_constant<(std::is_same_v<T, U> || ...)> {};

template <class To, class... T>
To narrow(const union_of<T...>& value) {
    return std::visit(
        [](const auto& inner) -> To {
            using member = std::decay_t<decltype(inner)>;
            if constexpr (in_union<member, To>::value) return To(std::in_place_type<member>, inner);
            else __builtin_unreachable();
        },
        value);
}

// T ! E : un succès de type T ou une erreur de type E.
template <class T, class E>
class result {
public:
    result() = default;

    static result ok(T value) { return result(std::in_place_index<0>, static_cast<T&&>(value)); }
    static result err(E error) { return result(std::in_place_index<1>, static_cast<E&&>(error)); }

    bool is_ok() const noexcept { return _value.index() == 0; }
    const T& value() const noexcept { return *std::get_if<0>(&_value); }
    const E& error() const noexcept { return *std::get_if<1>(&_value); }

    friend bool operator==(const result&, const result&) = default;

private:
    template <std::size_t I, class V>
    result(std::in_place_index_t<I> index, V&& value) : _value(index, std::forward<V>(value)) {}

    std::variant<T, E> _value;
};

}  // namespace rgl::standard
