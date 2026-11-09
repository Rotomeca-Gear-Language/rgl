#pragma once

// Le cycle de vie d'un mold à gear de fin (`mold Handle -> fermer { … }`) : la valeur n'a qu'un propriétaire,
// elle ne se copie jamais et son gear de fin s'appelle une fois, quand son dernier propriétaire la perd. Le
// struct généré porte une marque `lifecycle` : vraie tant que la valeur est à fermer, fausse une fois qu'elle a
// été déplacée ou fermée. Tout est dans l'en-tête ; un programme sans gear de fin ne l'inclut pas.

#include <utility>

namespace rgl::standard {

struct lifecycle {
    bool alive = true;

    lifecycle() = default;
    lifecycle(const lifecycle&) = delete;
    lifecycle& operator=(const lifecycle&) = delete;

    // Déplacer la marque : la source n'a plus rien à fermer.
    lifecycle(lifecycle&& other) noexcept : alive(other.alive) { other.alive = false; }
    lifecycle& operator=(lifecycle&& other) noexcept {
        alive = other.alive;
        other.alive = false;
        return *this;
    }
};

} // namespace rgl::standard
