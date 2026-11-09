#pragma once

// Les fichiers entiers : `var texte <- raw "a.txt";` lit un fichier en `string` (UTF-8 strict, BOM
// retiré) ou en `bytes`, et `valeur -> raw "a.txt";` l'écrit. Le chemin est en UTF-8 ; sous Windows
// il est converti en UTF-16 pour l'API du système. Rien ne s'arrête ni ne lève : chaque fonction
// rend un `status`, que le code généré transforme en `IoError` quand il n'est pas `ok`.

#include <cstddef>
#include <cstdint>
#include <memory>

#include "rgl/standard/array.hpp"
#include "rgl/standard/bytes.hpp"
#include "rgl/standard/nullable.hpp"
#include "rgl/standard/string.hpp"

namespace rgl::standard::file {

enum class status {
    ok,
    not_found,      // le fichier ou un dossier du chemin n'existe pas
    access_denied,  // droits refusés, fichier verrouillé, ou chemin qui est un dossier
    invalid_utf8,   // la lecture en texte a rencontré des octets qui ne sont pas de l'UTF-8
    failed,         // toute autre erreur du système, ou chemin qui n'est pas de l'UTF-8
    closed,         // le flux a été fermé (`close`)
    wrong_mode,     // le flux n'est pas ouvert pour cette opération (lire un flux ouvert en écriture, ou l'inverse)
};

// Lit tout le fichier en texte. Un BOM UTF-8 en tête est retiré ; des octets invalides donnent
// `invalid_utf8` et laissent `text` inchangé.
status read_text(const string& path, string& text);

// Lit tout le fichier, octet pour octet, BOM compris.
status read_bytes(const string& path, array<std::uint8_t>& bytes);

// Écrit `size` octets dans le fichier, créé ou remplacé (pas de renommage : le fichier existant est
// tronqué puis écrit).
status write(const string& path, const char* data, std::size_t size);

inline status write_text(const string& path, const string& text) { return write(path, text.data(), text.size()); }
// Écrit des octets : un tableau de ubyte de toute sorte (array, fixed_array, bounded_array…).
template <class Bytes>
status write_bytes(const string& path, const Bytes& bytes) {
    return write(path, reinterpret_cast<const char*>(bytes.data()), bytes.size());
}

// Le message d'une erreur, en français, pour le `message` de l'`IoError`.
string message(status result);

// Comment un flux s'ouvre : `file "a.txt"` lit, `file<write> "a.txt"` crée ou remplace le fichier, `file<append> "a.txt"` écrit à sa fin.
enum class open_mode { read, write, append };

// Un fichier ouvert (`FileStream`). Les copies partagent le même fichier : il se ferme quand la dernière copie disparaît, ou tout de suite par
// `close`, pour toutes les copies à la fois. Un flux en lecture lit des lignes (UTF-8 strict, `\n` ou `\r\n` retiré, un éventuel BOM n'est pas retiré)
// ou des octets ; un flux en écriture écrit à la suite. Les méthodes sont `const` : c'est le fichier qui change, pas la copie.
class stream {
   public:
    // Un flux jamais ouvert : toute opération rend `closed`.
    stream() = default;

    // Le chemin donné à l'ouverture ; vide pour un flux jamais ouvert.
    string path() const;

    bool is_open() const noexcept;

    // La ligne suivante, ou `null` en fin de fichier.
    status read_line(nullable<string>& line) const;

    // Jusqu'à `count` octets : moins, ou aucun, en fin de fichier.
    status read_bytes(std::size_t count, array<std::uint8_t>& bytes) const;

    status write(const char* data, std::size_t size) const;
    status write_text(const string& text) const { return write(text.data(), text.size()); }
    template <class Bytes>
    status write_bytes(const Bytes& bytes) const {
        return write(reinterpret_cast<const char*>(bytes.data()), bytes.size());
    }

    // Ferme le fichier maintenant (sans effet s'il est déjà fermé).
    void close() const noexcept;

    friend bool operator==(const stream& left, const stream& right) noexcept { return left._state == right._state; }

   private:
    struct state;
    std::shared_ptr<state> _state;

    friend status open(const string& path, open_mode mode, stream& out);
};

// Ouvre le fichier.
status open(const string& path, open_mode mode, stream& out);

}  // namespace rgl::standard::file
