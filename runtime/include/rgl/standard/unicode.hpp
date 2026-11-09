#pragma once

// La normalisation Unicode : les quatre formes (NFC, NFD, NFKC, NFKD) sur du texte UTF-8. Une même lettre accentuée peut s'écrire d'un
// seul caractère (« é », U+00E9) ou de deux (« e » puis l'accent U+0301) : la normalisation les ramène à une forme unique, ce qui rend
// la comparaison, la recherche et le tri fiables. Les tables viennent de la base de caractères Unicode (voir `unicode_tables.hpp`) ;
// les syllabes Hangul sont calculées. Un octet qui n'est pas de l'UTF-8 valide est recopié tel quel.

#include <cstdint>
#include <string>

#include "rgl/standard/string.hpp"

namespace rgl::standard::unicode {

enum class form {
    nfc,   // décomposition canonique, puis recomposition : « é » en un seul caractère
    nfd,   // décomposition canonique : « é » en « e » et son accent
    nfkc,  // décomposition de compatibilité, puis recomposition : « ﬁ » devient « fi », « ① » devient « 1 »
    nfkd,  // décomposition de compatibilité
};

// Le texte mis dans la forme demandée.
string normalize(const string& text, form which);

// Le texte sans ses accents : décomposé (NFD) puis débarrassé des caractères combinants.
string strip_marks(const string& text);

// La classe de combinaison d'un caractère : 0 pour une lettre ordinaire, non nulle pour un accent ou un signe qui se pose sur la
// lettre précédente.
std::uint8_t combining_class(std::uint32_t code);

}  // namespace rgl::standard::unicode
