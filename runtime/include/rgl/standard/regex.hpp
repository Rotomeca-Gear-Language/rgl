#pragma once

// rgl::standard::regex : le type RGL regex, une expression régulière écrite en littéral (/motif/flags).
//
// Le moteur est CTRE (runtime/third_party/ctre) : le motif est un paramètre de modèle, compilé en
// code C++ par Clang, sans analyse à l'exécution. Le compilateur RGL écrit donc, pour chaque
// littéral, un petit struct qui donne ses deux moteurs (`first` : la première occurrence, `all` :
// toutes) :
//
//     struct rgl_re_0 {
//         static constexpr auto first = ctre::search<u8R"rgl(a+)rgl">;
//         static constexpr auto all = ctre::search_all<u8R"rgl(a+)rgl">;
//     };
//
// et la valeur `regex` est `make_regex<rgl_re_0, global>()` : quatre pointeurs de fonctions, copiable
// et comparable comme un pointeur, si bien que `regex r: /a/; r <- /b/;` est permis alors que les
// deux motifs sont deux types C++.
//
// Le texte est lu en UTF-8 : `.` et les classes `[é-ü]` voient un caractère Unicode, pas un octet.
// `\w`, `\d`, `\s` et l'insensibilité à la casse (flag i) ne connaissent que l'ASCII.
//
// Opérations (le flag g : toutes les occurrences, sinon la première) :
// - test(s) : vrai si le motif est quelque part dans s ;
// - match(s) : null sans occurrence, sinon [tout le texte trouvé, groupe 1, groupe 2…] — un groupe
//   qui n'a pas participé est la chaîne vide ;
// - find(s) : les textes trouvés (toutes avec g, au plus la première sinon) ;
// - replace(s, par) : s où chaque occurrence trouvée (toutes avec g, sinon la première) est
//   remplacée par `par`, où $0 est le texte trouvé, $1 à $9 les groupes (vide s'ils n'existent pas)
//   et $$ un dollar.

#define CTRE_ENABLE_UTF8_RANGE
#include <ctre.hpp>

#include <cstddef>
#include <string_view>
#include <utility>

#include "array.hpp"
#include "nullable.hpp"
#include "string.hpp"

namespace rgl::standard {

namespace primitives {

class regex {
   public:
    using test_function = bool (*)(const string&);
    using match_function = nullable<array<string>> (*)(const string&);
    using find_function = array<string> (*)(const string&);
    using replace_function = string (*)(const string&, const string&);

    constexpr regex() noexcept = default;
    constexpr regex(test_function test, match_function match, find_function find, replace_function replace) noexcept
        : test_(test), match_(match), find_(find), replace_(replace) {}

    bool test(const string& text) const { return test_(text); }
    nullable<array<string>> match(const string& text) const { return match_(text); }
    array<string> find(const string& text) const { return find_(text); }
    string replace(const string& text, const string& with) const { return replace_(text, with); }

    friend bool operator==(const regex&, const regex&) = default;

   private:
    test_function test_ = nullptr;
    match_function match_ = nullptr;
    find_function find_ = nullptr;
    replace_function replace_ = nullptr;
};

}  // namespace primitives

using primitives::regex;

namespace detail {

inline std::u8string_view regex_view(const string& text) noexcept {
    return std::u8string_view(reinterpret_cast<const char8_t*>(text.data()), text.size());
}

inline string regex_text(std::u8string_view view) {
    return string(reinterpret_cast<const char*>(view.data()), view.size());
}

// Les groupes d'une occurrence : le texte entier puis chaque groupe, vide s'il n'a pas participé.
template <class Match>
array<string> regex_groups(const Match& found) {
    array<string> groups;
    [&]<std::size_t... I>(std::index_sequence<I...>) {
        (groups.push(regex_text(found.template get<I>().to_view())), ...);
    }(std::make_index_sequence<Match::count()>{});
    return groups;
}

template <class Re>
bool regex_test(const string& text) {
    return static_cast<bool>(Re::first(regex_view(text)));
}

template <class Re>
nullable<array<string>> regex_match(const string& text) {
    const auto found = Re::first(regex_view(text));
    if (!found) return nullable<array<string>>();
    return nullable<array<string>>(regex_groups(found));
}

template <class Re, bool Global>
array<string> regex_find(const string& text) {
    array<string> found;
    for (const auto& one : Re::all(regex_view(text))) {
        found.push(regex_text(one.template get<0>().to_view()));
        if constexpr (!Global) break;
    }
    return found;
}

// Écrit `with` dans `out` en remplaçant $0 à $9 par les groupes de l'occurrence, et $$ par $.
template <class Match>
void regex_expand(mstring& out, const string& with, const Match& found) {
    const char* text = with.data();
    const std::size_t size = with.size();
    for (std::size_t i = 0; i < size; ++i) {
        if (text[i] != '$' || i + 1 >= size) {
            out.append(text + i, 1);
            continue;
        }
        const char next = text[i + 1];
        if (next == '$') {
            out.append("$", 1);
            ++i;
        } else if (next >= '0' && next <= '9') {
            const std::size_t group = static_cast<std::size_t>(next - '0');
            [&]<std::size_t... I>(std::index_sequence<I...>) {
                ((I == group ? (out.append(regex_text(found.template get<I>().to_view())), 0) : 0), ...);
            }(std::make_index_sequence<Match::count()>{});
            ++i;
        } else {
            out.append(text + i, 1);
        }
    }
}

template <class Re, bool Global>
string regex_replace(const string& text, const string& with) {
    const auto view = regex_view(text);
    mstring out;
    const char8_t* copied = view.data();
    for (const auto& one : Re::all(view)) {
        const auto whole = one.template get<0>();
        const char8_t* begin = whole.to_view().data();
        const char8_t* end = begin + whole.to_view().size();
        out.append(reinterpret_cast<const char*>(copied), static_cast<std::size_t>(begin - copied));
        regex_expand(out, with, one);
        copied = end;
        if constexpr (!Global) break;
    }
    out.append(reinterpret_cast<const char*>(copied), static_cast<std::size_t>(view.data() + view.size() - copied));
    return out.to_immutable();
}

}  // namespace detail

// La valeur d'un littéral : `Re` est le struct écrit pour lui par le compilateur.
template <class Re, bool Global>
constexpr regex make_regex() noexcept {
    return regex(&detail::regex_test<Re>, &detail::regex_match<Re>, &detail::regex_find<Re, Global>,
                 &detail::regex_replace<Re, Global>);
}

}  // namespace rgl::standard
