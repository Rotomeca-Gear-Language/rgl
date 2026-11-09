#pragma once

// Une position dans le source RGL, tenue dans 32 bits : le fichier (6 bits), la ligne (18) et la colonne (8). Le code généré
// la passe aux opérations qui peuvent arrêter le programme ; c'est une constante immédiate, là où un texte « main.rgl:44:19 »
// coûte son adresse et ses octets dans l'exécutable à chaque opération. Une valeur trop grande est ramenée au maximum du champ
// (un fichier de données de plus de 262 143 lignes, une colonne au-delà de 255) : la position est alors approchée, jamais fausse
// au point de désigner un autre fichier. Les noms des fichiers sont ceux de `position_files`, que le programme généré définit.

#include <cstdint>
#include <cstdio>

namespace rgl::standard {

using position = std::uint32_t;

// Les noms des fichiers, dans l'ordre de leur numéro ; défini par le programme généré (un tableau fini par un pointeur nul).
extern const char* const position_files[];

namespace detail {

// Écrit « fichier:ligne:colonne » dans un tampon de 320 octets au moins, et le rend.
inline const char* position_text(position where, char (&buffer)[320]) noexcept {
    const unsigned file = (where >> 26) & 0x3f;
    const unsigned line = (where >> 8) & 0x3ffff;
    const unsigned column = where & 0xff;
    const char* name = "?";
    for (unsigned i = 0; position_files[i] != nullptr; ++i) {
        if (i == file) {
            name = position_files[i];
            break;
        }
    }
    std::snprintf(buffer, sizeof buffer, "%.256s:%u:%u", name, line, column);
    return buffer;
}

}  // namespace detail

}  // namespace rgl::standard
