#pragma once

// JSON : `var c <- json<Config> "a.json";` et `c -> json "a.json";`. Le runtime ne connaît pas les molds
// (ni réflexion, ni RTTI) : il lit le fichier en un arbre de valeurs neutre (`value`, analyseur de
// json.cpp), et le compilateur écrit, pour chaque type lu ou écrit, une fonction qui descend dans l'arbre
// (ou le construit) en s'appuyant sur les petites fonctions de ce fichier. Une erreur porte toujours le
// chemin de la valeur fautive : « $.joueurs[2].niveau : attendu un int, reçu une chaîne ».
//
// Aucune exception : chaque fonction rend faux et note son message dans le `context`.

#include <cmath>
#include <charconv>
#include <concepts>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <type_traits>
#include <vector>

#include "rgl/standard/file.hpp"
#include "rgl/standard/format.hpp"
#include "rgl/standard/nullable.hpp"
#include "rgl/standard/string.hpp"

namespace rgl::standard::json {

// Profondeur maximale d'un document : au-delà, l'analyseur refuse (une récursion sans limite
// épuiserait la pile sur un document hostile).
inline constexpr int max_depth = 256;

enum class kind { null, boolean, integer, real, text, array, object };

// Une valeur JSON. Un entier garde son signe et sa valeur absolue (de quoi tenir un int64 comme un
// uint64) ; un nombre avec point ou exposant, ou trop grand pour un entier, est un flottant.
struct value {
    kind type = kind::null;
    bool flag = false;
    bool negative = false;
    std::uint64_t magnitude = 0;
    double number = 0.0;
    string text;
    // Les noms des membres d'un objet, dans l'ordre du document ; leurs valeurs sont dans items.
    std::vector<string> keys;
    // Les éléments d'un tableau, ou les valeurs d'un objet.
    std::vector<value> items;
};

// L'état d'une lecture ou d'une écriture : le chemin de la valeur courante, et l'erreur.
class context {
   public:
    context() { path_.append("$", 1); }

    std::size_t mark() const noexcept { return path_.size(); }
    void reset(std::size_t mark) { path_.resize(mark); }

    void key(const string& name) {
        path_.push_back('.');
        path_.append(name.data(), name.size());
    }
    void key(const char* name) {
        path_.push_back('.');
        path_.append(name);
    }
    void index(std::size_t position) {
        char buffer[24];
        const auto result = std::to_chars(buffer, buffer + sizeof buffer, position);
        path_.push_back('[');
        path_.append(buffer, static_cast<std::size_t>(result.ptr - buffer));
        path_.push_back(']');
    }

    // Le chemin courant : « $.joueurs[2].niveau ».
    string path() const { return path_.to_immutable(); }

    // Note l'erreur, précédée du chemin courant ; renvoie faux.
    bool fail(const string& what) {
        message_ = interpolate(path(), string(" : "), what);
        failed_path_ = path();
        return false;
    }
    bool fail(const char* what) { return fail(string(what)); }

    // Note une erreur d'analyse du document lui-même (sans chemin).
    bool fail_syntax(const string& what) {
        message_ = what;
        failed_path_ = path();
        return false;
    }

    // Le message de la dernière erreur, et le chemin de la valeur fautive.
    const string& message() const noexcept { return message_; }
    const string& failed_path() const noexcept { return failed_path_; }

   private:
    mstring path_;
    string message_;
    string failed_path_;
};

// Ce qu'est une valeur, pour un message : « un entier », « une chaîne »…
inline const char* describe(const value& v) noexcept {
    switch (v.type) {
        case kind::null: return "null";
        case kind::boolean: return "un booléen";
        case kind::integer: return "un entier";
        case kind::real: return "un nombre";
        case kind::text: return "une chaîne";
        case kind::array: return "un tableau";
        default: return "un objet";
    }
}

// « attendu un int, reçu une chaîne » : l'erreur d'une valeur qui n'a pas le type attendu.
inline bool mismatch(const value& v, context& c, const char* expected) {
    return c.fail(interpolate(string("attendu "), string(expected), string(", reçu "), string(describe(v))));
}

// Lit le document dans `text` ; faux avec la ligne et la colonne de l'erreur dans `c` (json.cpp).
bool parse(const string& text, value& out, context& c);

// ---- Lecture : chaque fonction construit la valeur dans `out` ou note l'erreur.

inline bool read_bool(const value& v, nullable<bool>& out, context& c) {
    if (v.type != kind::boolean) return mismatch(v, c, "un bool");
    out.emplace(v.flag);
    return true;
}

template <std::integral T>
    requires(!std::same_as<T, bool>)
bool read_integer(const value& v, nullable<T>& out, context& c, const char* name) {
    if (v.type != kind::integer) return mismatch(v, c, name);
    if constexpr (std::is_signed_v<T>) {
        if (v.negative) {
            const std::uint64_t limit = static_cast<std::uint64_t>(std::numeric_limits<T>::max()) + 1;
            if (v.magnitude > limit) return c.fail(interpolate(string("l'entier ne tient pas dans un "), string(name)));
            out.emplace(static_cast<T>(static_cast<std::int64_t>(0 - v.magnitude)));
            return true;
        }
    } else if (v.negative && v.magnitude != 0) {
        return c.fail(interpolate(string("l'entier ne tient pas dans un "), string(name)));
    }
    if (v.magnitude > static_cast<std::uint64_t>(std::numeric_limits<T>::max()))
        return c.fail(interpolate(string("l'entier ne tient pas dans un "), string(name)));
    out.emplace(static_cast<T>(v.magnitude));
    return true;
}

template <std::floating_point T>
bool read_real(const value& v, nullable<T>& out, context& c, const char* name) {
    double number;
    if (v.type == kind::real) number = v.number;
    else if (v.type == kind::integer) number = v.negative ? -static_cast<double>(v.magnitude) : static_cast<double>(v.magnitude);
    else return mismatch(v, c, name);
    if constexpr (std::is_same_v<T, float>) {
        if (std::isfinite(number) && std::fabs(number) > static_cast<double>(std::numeric_limits<float>::max()))
            return c.fail("le nombre ne tient pas dans un float");
    }
    out.emplace(static_cast<T>(number));
    return true;
}

inline bool read_string(const value& v, nullable<string>& out, context& c) {
    if (v.type != kind::text) return mismatch(v, c, "une string");
    out.emplace(v.text);
    return true;
}

inline bool read_mstring(const value& v, nullable<mstring>& out, context& c) {
    if (v.type != kind::text) return mismatch(v, c, "une mstring");
    out.emplace(v.text);
    return true;
}

// Le membre `name` d'un objet, ou nullptr.
inline const value* member(const value& object, const char* name) noexcept {
    const std::size_t size = std::char_traits<char>::length(name);
    // Le dernier membre de ce nom l'emporte, comme dans la plupart des lecteurs de JSON.
    for (std::size_t i = object.keys.size(); i-- > 0;) {
        if (object.keys[i].size() == size && std::char_traits<char>::compare(object.keys[i].data(), name, size) == 0) return &object.items[i];
    }
    return nullptr;
}

inline bool missing(context& c, const char* field) {
    return c.fail(interpolate(string("champ « "), string(field), string(" » manquant")));
}

// ---- Écriture : le texte est indenté de deux espaces, un membre ou un élément par ligne.

inline void newline(mstring& out, int depth) {
    out.push_back('\n');
    for (int i = 0; i < depth; ++i) out.append("  ", 2);
}

inline void write_bool(mstring& out, bool v) { v ? out.append("true", 4) : out.append("false", 5); }

template <std::integral T>
    requires(!std::same_as<T, bool>)
void write_integer(mstring& out, T v) {
    char buffer[24];
    const auto result = std::to_chars(buffer, buffer + sizeof buffer, v);
    out.append(buffer, static_cast<std::size_t>(result.ptr - buffer));
}

// Un flottant : le texte le plus court qui relit la même valeur, avec un point ou un exposant (2.0). JSON
// n'a ni infini ni NaN : ils échouent.
template <std::floating_point T>
bool write_real(mstring& out, T v, context& c) {
    if (!std::isfinite(v)) return c.fail("un nombre infini ou NaN ne s'écrit pas en JSON");
    char buffer[40];
    const auto result = std::to_chars(buffer, buffer + sizeof buffer, v);
    const auto size = static_cast<std::size_t>(result.ptr - buffer);
    out.append(buffer, size);
    for (std::size_t i = 0; i < size; ++i) {
        if (buffer[i] == '.' || buffer[i] == 'e') return true;
    }
    out.append(".0", 2);
    return true;
}

// Une chaîne entre guillemets : « " », « \ » et les caractères de contrôle échappés, le reste en UTF-8.
inline void write_string(mstring& out, const char* data, std::size_t size) {
    out.push_back('"');
    for (std::size_t i = 0; i < size; ++i) {
        const auto ch = static_cast<unsigned char>(data[i]);
        switch (ch) {
            case '"': out.append("\\\"", 2); break;
            case '\\': out.append("\\\\", 2); break;
            case '\n': out.append("\\n", 2); break;
            case '\r': out.append("\\r", 2); break;
            case '\t': out.append("\\t", 2); break;
            case '\b': out.append("\\b", 2); break;
            case '\f': out.append("\\f", 2); break;
            default:
                if (ch < 0x20) {
                    static constexpr char digits[] = "0123456789abcdef";
                    const char escape[6] = {'\\', 'u', '0', '0', digits[ch >> 4], digits[ch & 0xF]};
                    out.append(escape, 6);
                } else {
                    out.push_back(static_cast<char>(ch));
                }
        }
    }
    out.push_back('"');
}
inline void write_string(mstring& out, const string& text) { write_string(out, text.data(), text.size()); }
inline void write_string(mstring& out, const mstring& text) { write_string(out, text.data(), text.size()); }

// ---- Fichiers

// Le résultat d'une lecture ou d'une écriture : réussi, échec du fichier (`io_status`), ou échec du
// contenu (le message est dans le `context`).
struct outcome {
    bool ok;
    bool io;
    file::status io_status;
};

// Lit le fichier, l'analyse, puis le descend avec `reader(value, nullable<T>&, context&)`.
template <class T, class Reader>
outcome read(const string& path, context& c, nullable<T>& out, Reader reader) {
    string text;
    if (const auto status = file::read_text(path, text); status != file::status::ok) return {false, true, status};

    value root;
    if (!parse(text, root, c) || !reader(root, out, c)) return {false, false, file::status::ok};
    return {true, false, file::status::ok};
}

// Analyse un texte déjà en mémoire puis le descend avec `reader` ; faux avec l'erreur dans `c`.
template <class T, class Reader>
bool from_text(const string& text, context& c, nullable<T>& out, Reader reader) {
    value root;
    return parse(text, root, c) && reader(root, out, c);
}

// Écrit le texte JSON, suivi d'un retour à la ligne.
inline outcome write_file(const string& path, mstring& text) {
    text.push_back('\n');
    const auto status = file::write(path, text.data(), text.size());
    return {status == file::status::ok, status != file::status::ok, status};
}

}  // namespace rgl::standard::json
