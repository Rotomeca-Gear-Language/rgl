#pragma once

// Les opérations de texte de la bibliothèque standard de RGL, écrites en un seul passage sur les octets : sous-chaîne, recherche, découpe,
// jonction, remplacement, majuscules, répétition, inversion. Les rangs et les longueurs sont des caractères Unicode (des points de code), comme
// partout en RGL ; les recherches se font sur les octets, ce qui est exact puisqu'un texte UTF-8 valide ne contient un motif valide qu'à une
// frontière de caractère. Un texte sans caractère multi-octet (string::is_ascii) est parcouru sans rien compter : rang de caractère et rang
// d'octet sont alors les mêmes.
//
// Chaque fonction lit ses arguments sans les modifier et construit son résultat d'un coup (une allocation, aucune jusqu'à 22 octets).
// Elle a le même résultat que le gear RGL qu'elle remplace, qui reconstruisait le texte caractère par caractère.

#include <cstddef>
#include <string_view>

#include "rgl/standard/array.hpp"
#include "rgl/standard/string.hpp"

namespace rgl::standard::text {

namespace detail {

inline bool continuation(char byte) noexcept { return (static_cast<unsigned char>(byte) & 0xC0) == 0x80; }

// Le rang d'octet du caractère de rang rank (length pour la fin du texte), en partant de l'octet start de rang rank0.
inline std::size_t byte_after(const string& s, std::size_t byte, std::size_t from_rank, std::size_t rank) noexcept {
    const char* data = s.data();
    const std::size_t size = s.size();
    while (from_rank < rank && byte < size) {
        ++byte;
        while (byte < size && continuation(data[byte])) ++byte;
        ++from_rank;
    }
    return byte;
}

// Le rang de caractère de l'octet byte : le nombre de caractères qui commencent avant lui.
inline std::size_t rank_of_byte(const string& s, std::size_t byte) noexcept {
    if (s.is_ascii()) return byte;
    std::size_t rank = 0;
    for (std::size_t i = 0; i < byte; ++i) rank += !continuation(s.data()[i]);
    return rank;
}

}  // namespace detail

// Les caractères de start (inclus) à stop (exclu) : un rang négatif compte depuis la fin, un rang hors du texte est ramené à ses bords, et une
// fin avant le début donne un texte vide.
inline string substring(const string& s, long long start, long long stop) {
    const long long n = static_cast<long long>(s.length());
    const long long from0 = start < 0 ? start + n : start;
    const long long to0 = stop < 0 ? stop + n : stop;
    const long long from = from0 < 0 ? 0 : (from0 > n ? n : from0);
    const long long to = to0 < from ? from : (to0 > n ? n : to0);
    if (from == to) return string();
    if (s.is_ascii()) return string(s.data() + from, static_cast<std::size_t>(to - from));

    const std::size_t a = detail::byte_after(s, 0, 0, static_cast<std::size_t>(from));
    const std::size_t b = detail::byte_after(s, a, static_cast<std::size_t>(from), static_cast<std::size_t>(to));
    return string(s.data() + a, b - a);
}

// Le rang du premier caractère où what commence, ou -1. Un motif vide commence au rang 0.
inline long long index_of(const string& s, const string& what) noexcept {
    const std::size_t found = s.view().find(what.view());
    return found == std::string_view::npos ? -1 : static_cast<long long>(detail::rank_of_byte(s, found));
}

// Le rang du dernier caractère où what commence, ou -1. Un motif vide commence à la fin : sa longueur.
inline long long last_index_of(const string& s, const string& what) noexcept {
    const std::size_t found = s.view().rfind(what.view());
    return found == std::string_view::npos ? -1 : static_cast<long long>(detail::rank_of_byte(s, found));
}

// Les morceaux de s séparés par separator, séparateurs retirés, de gauche à droite sans se recouvrir. Sans séparateur (vide), le texte entier.
inline array<string> split(const string& s, const string& separator) {
    array<string> parts;
    if (separator.size() == 0) {
        parts.push(s);
        return parts;
    }
    const std::string_view text = s.view();
    const std::string_view cut = separator.view();
    std::size_t start = 0;
    for (std::size_t found = text.find(cut); found != std::string_view::npos; found = text.find(cut, start)) {
        parts.push(string(text.data() + start, found - start));
        start = found + cut.size();
    }
    parts.push(string(text.data() + start, text.size() - start));
    return parts;
}

// Les textes de parts mis bout à bout, séparés par separator : la taille est calculée d'abord, le résultat écrit d'un coup.
inline string join(const array<string>& parts, const string& separator) {
    if (parts.size() == 0) return string();
    std::size_t total = separator.size() * (parts.size() - 1);
    for (std::size_t i = 0; i < parts.size(); ++i) total += parts[i].size();
    return string::with_size(total, [&](char* target) {
        for (std::size_t i = 0; i < parts.size(); ++i) {
            if (i > 0 && separator.size() != 0) {
                for (std::size_t j = 0; j < separator.size(); ++j) *target++ = separator.data()[j];
            }
            const string& part = parts[i];
            for (std::size_t j = 0; j < part.size(); ++j) *target++ = part.data()[j];
        }
    });
}

// s où chaque occurrence de what (sans recouvrement, de gauche à droite) est remplacée par by. Un motif vide ne remplace rien.
inline string replace(const string& s, const string& what, const string& by) {
    if (what.size() == 0) return s;
    const std::string_view text = s.view();
    const std::string_view cut = what.view();
    std::size_t found = text.find(cut);
    if (found == std::string_view::npos) return s;

    std::size_t occurrences = 0;
    for (std::size_t at = found; at != std::string_view::npos; at = text.find(cut, at + cut.size())) ++occurrences;
    const std::size_t total = text.size() - occurrences * cut.size() + occurrences * by.size();

    return string::with_size(total, [&](char* target) {
        std::size_t start = 0;
        for (; found != std::string_view::npos; found = text.find(cut, start)) {
            for (std::size_t i = start; i < found; ++i) *target++ = text[i];
            for (std::size_t i = 0; i < by.size(); ++i) *target++ = by.data()[i];
            start = found + cut.size();
        }
        for (std::size_t i = start; i < text.size(); ++i) *target++ = text[i];
    });
}

// Les lettres ASCII en majuscules (ou en minuscules) ; tout le reste, caractères multi-octets compris, est inchangé.
inline string upper(const string& s) {
    bool changes = false;
    for (std::size_t i = 0; i < s.size() && !changes; ++i) changes = s.data()[i] >= 'a' && s.data()[i] <= 'z';
    if (!changes) return s;
    return string::with_size(s.size(), [&](char* target) {
        for (std::size_t i = 0; i < s.size(); ++i) {
            const char c = s.data()[i];
            target[i] = (c >= 'a' && c <= 'z') ? static_cast<char>(c - 'a' + 'A') : c;
        }
    });
}

inline string lower(const string& s) {
    bool changes = false;
    for (std::size_t i = 0; i < s.size() && !changes; ++i) changes = s.data()[i] >= 'A' && s.data()[i] <= 'Z';
    if (!changes) return s;
    return string::with_size(s.size(), [&](char* target) {
        for (std::size_t i = 0; i < s.size(); ++i) {
            const char c = s.data()[i];
            target[i] = (c >= 'A' && c <= 'Z') ? static_cast<char>(c - 'A' + 'a') : c;
        }
    });
}

// s répété count fois.
inline string repeat(const string& s, unsigned long long count) {
    if (count == 0 || s.size() == 0) return string();
    const std::size_t total = s.size() * static_cast<std::size_t>(count);
    return string::with_size(total, [&](char* target) {
        for (unsigned long long k = 0; k < count; ++k) {
            for (std::size_t i = 0; i < s.size(); ++i) *target++ = s.data()[i];
        }
    });
}

// Les caractères de s dans l'ordre inverse (par point de code : un caractère multi-octet reste entier).
inline string reverse(const string& s) {
    if (s.size() <= 1) return s;
    return string::with_size(s.size(), [&](char* target) {
        const char* data = s.data();
        std::size_t end = s.size();
        while (end > 0) {
            std::size_t begin = end - 1;
            while (begin > 0 && detail::continuation(data[begin])) --begin;
            for (std::size_t i = begin; i < end; ++i) *target++ = data[i];
            end = begin;
        }
    });
}

}  // namespace rgl::standard::text
