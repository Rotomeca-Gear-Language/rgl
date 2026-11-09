#pragma once

// Les datasets (`dataset Moves of Move { … }`) : l'identifiant d'une entrée est sa position, et la constante des noms dit
// laquelle est laquelle. `find` retrouve l'identifiant d'un nom venu de l'exécution (une sauvegarde, une saisie).
// Tout est dans l'en-tête ; un programme qui ne cherche jamais un nom ne l'inclut pas.

#include <cstdint>

#include "rgl/standard/nullable.hpp"

namespace rgl::standard {

// L'identifiant de l'entrée de ce nom, ou rien. `names` est un tableau de chaînes, `wanted` une chaîne.
template <class Names, class Text>
nullable<std::uint32_t> dataset_find(const Names& names, const Text& wanted) {
    for (std::uint32_t i = 0; i < static_cast<std::uint32_t>(names.size()); ++i) {
        if (names[i] == wanted) return i;
    }
    return nullable<std::uint32_t>{};
}

} // namespace rgl::standard
