#pragma once

// rgl::standard::nullable<T>, le T? de RGL : une valeur de T, ou null. Un alias de std::optional, qui
// ajoute à T un indicateur (et le padding qui va avec). Quand le compilateur sait qu'une valeur de T
// n'est jamais utilisée, il écrit à la place rgl::standard::niche_nullable<T, Sentinel> : null est cette valeur
// inutilisée, le T? a la taille de T et aucun indicateur. Les deux se lisent de la même façon
// (has_value, *, value, value_or, emplace, reset, comparaisons). Tout est dans l'en-tête : un
// programme sans nullable ne l'inclut pas.

#include <cstdint>
#include <optional>
#include <type_traits>
#include <utility>

namespace rgl::standard {

template <class T>
using nullable = std::optional<T>;

namespace detail {

// Ce qui range une valeur de T dans la niche : l'entier lui-même, son type sous-jacent pour un enum, un octet pour un bool
// (un bool ne peut pas valoir 2 sans comportement indéfini).
template <class T>
struct niche_storage {
    using type = T;
};
template <>
struct niche_storage<bool> {
    using type = std::uint8_t;
};

template <class T>
    requires std::is_enum_v<T>
struct niche_storage<T> {
    using under = std::underlying_type_t<T>;
    using type = std::conditional_t<std::is_same_v<under, bool>, std::uint8_t, under>;
};

}  // namespace detail

// Un T? dont null est une valeur de T que le programme n'utilise jamais : un bool (2), un enum (une valeur qu'aucune variante n'a), un
// entier borné (juste au-delà d'une borne). Sentinel est écrit dans le type de ce qui range la valeur.
template <class T, auto Sentinel>
class niche_nullable {
    using storage = typename detail::niche_storage<T>::type;

   public:
    using value_type = T;

    constexpr niche_nullable() noexcept = default;
    constexpr niche_nullable(std::nullopt_t) noexcept {}
    constexpr niche_nullable(T value) noexcept : raw_(static_cast<storage>(value)) {}
    constexpr niche_nullable(const std::optional<T>& other) noexcept : raw_(other.has_value() ? static_cast<storage>(*other) : static_cast<storage>(Sentinel)) {}

    constexpr bool has_value() const noexcept { return raw_ != static_cast<storage>(Sentinel); }
    constexpr explicit operator bool() const noexcept { return has_value(); }

    // La valeur, que l'appelant a vérifiée (il a testé has_value).
    constexpr T operator*() const noexcept { return static_cast<T>(raw_); }
    constexpr T value() const noexcept { return static_cast<T>(raw_); }
    template <class U>
    constexpr T value_or(U&& fallback) const noexcept {
        return has_value() ? static_cast<T>(raw_) : static_cast<T>(static_cast<U&&>(fallback));
    }

    constexpr void emplace(T value) noexcept { raw_ = static_cast<storage>(value); }
    constexpr void reset() noexcept { raw_ = static_cast<storage>(Sentinel); }

    friend constexpr bool operator==(const niche_nullable&, const niche_nullable&) noexcept = default;
    friend constexpr bool operator==(const niche_nullable& left, std::nullopt_t) noexcept { return !left.has_value(); }
    friend constexpr bool operator==(const niche_nullable& left, T right) noexcept { return left.has_value() && static_cast<T>(left.raw_) == right; }

   private:
    storage raw_ = static_cast<storage>(Sentinel);
};

// Une niche ne coûte rien : un bool? tient dans un octet.
static_assert(sizeof(niche_nullable<bool, 2>) == sizeof(bool), "un bool? avec niche a la taille d'un bool");

}  // namespace rgl::standard
