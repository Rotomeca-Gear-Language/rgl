#pragma once

// Les `bytes` de RGL sont un `array<std::uint8_t>`. Ce que le texte et les octets échangent :
// `texte as bytes` donne les octets UTF-8 de la chaîne, `octets as? string` la chaîne qu'ils forment
// quand ce sont des octets UTF-8 valides (null sinon). Tout est dans l'en-tête.

#include <cstddef>
#include <cstdint>

#include "rgl/standard/array.hpp"
#include "rgl/standard/nullable.hpp"
#include "rgl/standard/string.hpp"

namespace rgl::standard::bytes {

// Vrai si `data` est de l'UTF-8 valide : ni octet isolé, ni séquence trop longue, ni substitut, ni
// point de code au-delà de U+10FFFF.
inline bool is_valid_utf8(const char* data, std::size_t size) {
    const auto* octets = reinterpret_cast<const unsigned char*>(data);
    std::size_t i = 0;
    while (i < size) {
        const unsigned char lead = octets[i];
        if (lead < 0x80) {
            ++i;
            continue;
        }

        std::size_t extra;
        std::uint32_t point;
        if (lead >= 0xC2 && lead <= 0xDF) {
            extra = 1;
            point = lead & 0x1Fu;
        } else if (lead >= 0xE0 && lead <= 0xEF) {
            extra = 2;
            point = lead & 0x0Fu;
        } else if (lead >= 0xF0 && lead <= 0xF4) {
            extra = 3;
            point = lead & 0x07u;
        } else {
            return false;
        }
        if (size - i <= extra) return false;

        for (std::size_t k = 1; k <= extra; ++k) {
            const unsigned char next = octets[i + k];
            if ((next & 0xC0) != 0x80) return false;
            point = (point << 6) | (next & 0x3Fu);
        }
        // Séquence trop longue pour sa valeur, substitut UTF-16, ou au-delà de U+10FFFF.
        if (extra == 2 && point < 0x800) return false;
        if (extra == 3 && (point < 0x10000 || point > 0x10FFFF)) return false;
        if (point >= 0xD800 && point <= 0xDFFF) return false;
        i += extra + 1;
    }
    return true;
}

// Les octets UTF-8 d'une chaîne.
inline array<std::uint8_t> from_string(const string& text) {
    array<std::uint8_t> result;
    result.reserve(text.size());
    for (std::size_t i = 0; i < text.size(); ++i) result.push(static_cast<std::uint8_t>(text.data()[i]));
    return result;
}

// Vrai si les octets (un tableau de ubyte de toute sorte) forment du texte UTF-8 valide.
template <class Bytes>
bool is_text(const Bytes& octets) {
    return is_valid_utf8(reinterpret_cast<const char*>(octets.data()), octets.size());
}

// La chaîne que forment des octets dont `is_text` est vrai.
template <class Bytes>
string to_string(const Bytes& octets) {
    return string(reinterpret_cast<const char*>(octets.data()), octets.size());
}

// La chaîne des octets, ou null s'ils ne sont pas de l'UTF-8 valide.
template <class Bytes>
nullable<string> try_to_string(const Bytes& octets) {
    if (!is_text(octets)) return std::nullopt;
    return to_string(octets);
}

}  // namespace rgl::standard::bytes
