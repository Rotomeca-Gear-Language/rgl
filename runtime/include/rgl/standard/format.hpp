#pragma once

// Écriture des valeurs en texte, pour les templates RGL (`total = {a + b}`). Un template devient
// rgl::standard::interpolate(morceau, morceau, …) : chaque morceau est une chaîne, un entier, un
// flottant, un bool, un symbole, un mold (une erreur comprise), un enum, un nullable (« null »), une union,
// un résultat ou un tableau, écrit à la suite des précédents. Tout est dans l'en-tête : un programme sans
// template n'inclut pas ce fichier.
//
// Un template est écrit en deux passes sur la même destination générique : la première compte les
// caractères (detail::text_size), la seconde les écrit directement dans la chaîne finale
// (detail::text_writer). Il n'y a donc qu'une allocation, et aucune pour une petite chaîne.

#include <charconv>
#include <concepts>
#include <cstddef>
#include <cstring>
#include <string_view>
#include <tuple>

#include "rgl/standard/nullable.hpp"
#include "rgl/standard/string.hpp"
#include "rgl/standard/datetime.hpp"
#include "rgl/standard/duration.hpp"
#include "rgl/standard/symbol.hpp"
#include "rgl/standard/union.hpp"

namespace rgl::standard {

namespace detail {

// Première passe : compte les caractères écrits.
struct text_size {
    std::size_t size = 0;

    void append(const char*, std::size_t count) noexcept { size += count; }
    void append(const char* text) noexcept { size += std::char_traits<char>::length(text); }
    void push_back(char) noexcept { ++size; }
};

// Seconde passe : écrit les caractères à la suite, dans une zone déjà de la bonne taille.
struct text_writer {
    char* at;

    void append(const char* text, std::size_t count) noexcept {
        if (count != 0) std::memcpy(at, text, count);
        at += count;
    }
    void append(const char* text) noexcept { append(text, std::char_traits<char>::length(text)); }
    void push_back(char character) noexcept { *at++ = character; }
};

template <class Out>
void write(Out& out, const string& text) {
    out.append(text.data(), text.size());
}
template <class Out>
void write(Out& out, const mstring& text) {
    out.append(text.data(), text.size());
}
// Un symbole s'écrit par son nom seul : `{§pret}` donne « pret ».
template <class Out>
void write(Out& out, symbol value) {
    out.append(value.data(), value.size());
}
// Une durée s'écrit par ses millisecondes : `{5s}` donne « 5000ms ».
template <class Out>
void write(Out& out, duration value) {
    char buffer[24];
    const auto result = std::to_chars(buffer, buffer + sizeof buffer, value.milliseconds());
    out.append(buffer, static_cast<std::size_t>(result.ptr - buffer));
    out.append("ms", 2);
}
// Une date s'écrit en ISO 8601, en UTC : `{d}` donne « 2026-10-24T10:30:00Z », avec « .mmm » si les
// millisecondes ne sont pas nulles.
template <class Out>
void write(Out& out, datetime value) {
    constexpr std::int64_t per_day = 86'400'000;
    std::int64_t days = value.milliseconds() / per_day;
    std::int64_t rest = value.milliseconds() % per_day;
    if (rest < 0) {
        rest += per_day;
        --days;
    }
    const auto date = detail::civil_from_days(days);
    if (date.year >= 0 && date.year <= 9999) {
        detail::datetime_digits(out, static_cast<std::uint64_t>(date.year), 4);
    } else {
        out.append(date.year < 0 ? "-" : "+", 1);
        detail::datetime_digits(out, static_cast<std::uint64_t>(date.year < 0 ? -date.year : date.year), 6);
    }
    out.append("-", 1);
    detail::datetime_digits(out, date.month, 2);
    out.append("-", 1);
    detail::datetime_digits(out, date.day, 2);
    out.append("T", 1);
    detail::datetime_digits(out, static_cast<std::uint64_t>(rest / 3'600'000), 2);
    out.append(":", 1);
    detail::datetime_digits(out, static_cast<std::uint64_t>(rest / 60'000 % 60), 2);
    out.append(":", 1);
    detail::datetime_digits(out, static_cast<std::uint64_t>(rest / 1000 % 60), 2);
    if (rest % 1000 != 0) {
        out.append(".", 1);
        detail::datetime_digits(out, static_cast<std::uint64_t>(rest % 1000), 3);
    }
    out.append("Z", 1);
}
template <class Out>
void write(Out& out, bool value) {
    value ? out.append("true", 4) : out.append("false", 5);
}

// Un mold ou un enum : le code généré lui donne une fonction rgl_write, trouvée par ADL.
template <class Out, class T>
    requires requires(Out& out, const T& value) { rgl_write(out, value); }
void write(Out& out, const T& value) {
    rgl_write(out, value);
}

// Un entier en décimal.
template <class Out, std::integral T>
    requires(!std::same_as<T, bool>)
void write(Out& out, T value) {
    char buffer[24];
    const auto result = std::to_chars(buffer, buffer + sizeof buffer, value);
    out.append(buffer, static_cast<std::size_t>(result.ptr - buffer));
}

// Un flottant : le texte le plus court qui relit exactement la même valeur, toujours avec un
// point ou un exposant (2.0, pas 2), pour qu'on le reconnaisse comme flottant.
template <class Out, std::floating_point T>
void write(Out& out, T value) {
    char buffer[32];
    const auto result = std::to_chars(buffer, buffer + sizeof buffer, value);
    const auto size = static_cast<std::size_t>(result.ptr - buffer);
    out.append(buffer, size);

    for (std::size_t i = 0; i < size; ++i) {
        // « . » ou exposant ; « inf » et « nan » restent tels quels.
        if (buffer[i] == '.' || buffer[i] == 'e' || buffer[i] == 'n') return;
    }
    out.append(".0", 2);
}

// Un nullable : sa valeur, ou « null ».
template <class Out, class T>
void write(Out& out, const nullable<T>& value) {
    if (value.has_value()) write(out, *value);
    else out.append("null", 4);
}

template <class Out, class T, auto Sentinel>
void write(Out& out, const niche_nullable<T, Sentinel>& value) {
    if (value.has_value()) write(out, *value);
    else out.append("null", 4);
}

// Une union : la valeur de son membre.
template <class Out, class... T>
void write(Out& out, const union_of<T...>& value);

// Un résultat : ok(valeur) ou err(erreur).
template <class Out, class T, class E>
void write(Out& out, const result<T, E>& value);

// Un tableau (tout type marqué rgl_sequence) : ses éléments entre crochets, écrits comme les champs
// d'un mold : [1, 2, "a"]. Déclaré ici pour qu'un tableau dans un mold ou un tableau s'écrive aussi.
template <class Out, class Items>
    requires requires { typename Items::rgl_sequence; }
void write(Out& out, const Items& items);

// Un dictionnaire (marqué rgl_dictionary) : [clé: valeur, …], rangé par clé ; vide, [:].
template <class Out, class Entries>
    requires requires { typename Entries::rgl_dictionary; }
void write(Out& out, const Entries& entries);

// Un tuple : (1, "a").
template <class Out, class... T>
void write(Out& out, const std::tuple<T...>& value);

// Un champ de mold : une chaîne entre guillemets, toute autre valeur comme dans un template.
template <class Out>
void write_field(Out& out, const string& text) {
    out.push_back('"');
    out.append(text.data(), text.size());
    out.push_back('"');
}
template <class Out>
void write_field(Out& out, const mstring& text) {
    out.push_back('"');
    out.append(text.data(), text.size());
    out.push_back('"');
}
template <class Out, class T>
void write_field(Out& out, const T& value) {
    write(out, value);
}
template <class Out, class T>
void write_field(Out& out, const nullable<T>& value) {
    if (value.has_value()) write_field(out, *value);
    else out.append("null", 4);
}

template <class Out, class T, auto Sentinel>
void write_field(Out& out, const niche_nullable<T, Sentinel>& value) {
    if (value.has_value()) write_field(out, *value);
    else out.append("null", 4);
}

template <class Out, class... T>
void write(Out& out, const union_of<T...>& value) {
    std::visit([&out](const auto& inner) { write_field(out, inner); }, value);
}

template <class Out, class Items>
    requires requires { typename Items::rgl_sequence; }
void write(Out& out, const Items& items) {
    out.push_back('[');
    bool first = true;
    for (const auto& item : items) {
        if (!first) out.append(", ", 2);
        first = false;
        write_field(out, item);
    }
    out.push_back(']');
}

template <class Out, class Entries>
    requires requires { typename Entries::rgl_dictionary; }
void write(Out& out, const Entries& entries) {
    if (entries.empty()) {
        out.append("[:]", 3);
        return;
    }
    out.push_back('[');
    bool first = true;
    for (const auto& [key, value] : entries) {
        if (!first) out.append(", ", 2);
        first = false;
        write_field(out, key);
        out.append(": ", 2);
        write_field(out, value);
    }
    out.push_back(']');
}

template <class Out, class... T>
void write(Out& out, const std::tuple<T...>& value) {
    out.push_back('(');
    std::apply([&out](const auto&... items) {
        bool first = true;
        ((first ? void() : out.append(", ", 2), first = false, write_field(out, items)), ...);
    }, value);
    out.push_back(')');
}

template <class Out, class T, class E>
void write(Out& out, const result<T, E>& value) {
    if (value.is_ok()) {
        out.append("ok(", 3);
        write_field(out, value.value());
    } else {
        out.append("err(", 4);
        write_field(out, value.error());
    }
    out.push_back(')');
}

}  // namespace detail

// Les morceaux d'un template, écrits à la suite : leur taille d'abord, puis une seule allocation.
template <class... Parts>
string interpolate(const Parts&... parts) {
    detail::text_size size;
    (detail::write(size, parts), ...);
    return string::with_size(size.size, [&](char* target) {
        detail::text_writer writer{target};
        (detail::write(writer, parts), ...);
    });
}

}  // namespace rgl::standard
