#pragma once

// La console RGL : `valeur -> console;` écrit la valeur telle quelle, sans retour à la ligne, et
// `var ligne <- console;` lit une ligne. Le texte est en UTF-8 ; sous Windows, une vraie console
// reçoit et rend de l'UTF-16 (WriteConsoleW, ReadConsoleW), quelle que soit sa page de code.

#include <cstddef>

#include "rgl/standard/string.hpp"

namespace rgl::standard::console {

// Écrit `size` octets UTF-8 sur la sortie standard : tout de suite dans une vraie console ; redirigée,
// par un tampon vidé quand il est plein, avant une lecture, à la fin du programme et sur un abort.
void write(const char* data, std::size_t size);

inline void write(const string& text) { write(text.data(), text.size()); }
inline void write(const mstring& text) { write(text.data(), text.size()); }

// Lit une ligne de l'entrée standard, sans son retour à la ligne (« \n » ou « \r\n ») ; en fin
// d'entrée, une chaîne vide.
string read_line();

// Vide le tampon de sortie : tout ce qui a été écrit est visible avant d'attendre l'utilisateur.
void flush();

// Lit tout le reste de l'entrée standard (jusqu'à la fin de l'entrée) ; sur une vraie console Windows, jusqu'à la première ligne vide.
string read_all();

}  // namespace rgl::standard::console
