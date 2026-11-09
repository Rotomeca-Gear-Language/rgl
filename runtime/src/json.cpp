// Composant « json » du runtime : l'analyseur d'un document JSON en arbre de valeurs (json.hpp).

#include "rgl/standard/json.hpp"

#include <charconv>
#include <system_error>

namespace rgl::standard::json {

namespace {

class parser {
   public:
    parser(const char* data, std::size_t size, context& c) : begin_(data), at_(data), end_(data + size), c_(c) {}

    bool run(value& out) {
        skip_space();
        if (at_ == end_) return error("document vide");
        if (!parse_value(out, 0)) return false;
        skip_space();
        if (at_ != end_) return error("texte inattendu après la valeur");
        return true;
    }

   private:
    const char* begin_;
    const char* at_;
    const char* end_;
    context& c_;

    // L'erreur, avec la ligne et la colonne (en octets) de l'endroit courant.
    bool error(const char* what) {
        std::size_t line = 1;
        std::size_t column = 1;
        for (const char* p = begin_; p < at_; ++p) {
            if (*p == '\n') {
                ++line;
                column = 1;
            } else {
                ++column;
            }
        }
        return c_.fail_syntax(interpolate(string("JSON invalide, ligne "), line, string(", colonne "), column, string(" : "), string(what)));
    }

    void skip_space() {
        while (at_ != end_ && (*at_ == ' ' || *at_ == '\t' || *at_ == '\n' || *at_ == '\r')) ++at_;
    }

    bool literal(const char* word, std::size_t size) {
        if (static_cast<std::size_t>(end_ - at_) < size || std::char_traits<char>::compare(at_, word, size) != 0) return false;
        at_ += size;
        return true;
    }

    bool parse_value(value& out, int depth) {
        if (depth > max_depth) return error("document trop profond");
        if (at_ == end_) return error("valeur attendue");

        switch (*at_) {
            case '{': return parse_object(out, depth);
            case '[': return parse_array(out, depth);
            case '"':
                out.type = kind::text;
                return parse_string(out.text);
            case 't':
                if (!literal("true", 4)) return error("valeur inconnue");
                out.type = kind::boolean;
                out.flag = true;
                return true;
            case 'f':
                if (!literal("false", 5)) return error("valeur inconnue");
                out.type = kind::boolean;
                out.flag = false;
                return true;
            case 'n':
                if (!literal("null", 4)) return error("valeur inconnue");
                out.type = kind::null;
                return true;
            default:
                if (*at_ == '-' || (*at_ >= '0' && *at_ <= '9')) return parse_number(out);
                return error("valeur inconnue");
        }
    }

    bool parse_object(value& out, int depth) {
        out.type = kind::object;
        ++at_;
        skip_space();
        if (at_ != end_ && *at_ == '}') {
            ++at_;
            return true;
        }
        while (true) {
            skip_space();
            if (at_ == end_ || *at_ != '"') return error("nom de membre attendu");
            string key;
            if (!parse_string(key)) return false;
            skip_space();
            if (at_ == end_ || *at_ != ':') return error("« : » attendu après le nom du membre");
            ++at_;
            skip_space();
            value item;
            if (!parse_value(item, depth + 1)) return false;
            out.keys.push_back(static_cast<string&&>(key));
            out.items.push_back(static_cast<value&&>(item));
            skip_space();
            if (at_ == end_) return error("« } » attendu");
            if (*at_ == ',') {
                ++at_;
                continue;
            }
            if (*at_ == '}') {
                ++at_;
                return true;
            }
            return error("« , » ou « } » attendu");
        }
    }

    bool parse_array(value& out, int depth) {
        out.type = kind::array;
        ++at_;
        skip_space();
        if (at_ != end_ && *at_ == ']') {
            ++at_;
            return true;
        }
        while (true) {
            skip_space();
            value item;
            if (!parse_value(item, depth + 1)) return false;
            out.items.push_back(static_cast<value&&>(item));
            skip_space();
            if (at_ == end_) return error("« ] » attendu");
            if (*at_ == ',') {
                ++at_;
                continue;
            }
            if (*at_ == ']') {
                ++at_;
                return true;
            }
            return error("« , » ou « ] » attendu");
        }
    }

    // Quatre chiffres hexadécimaux après « \u ».
    bool hex4(std::uint32_t& out) {
        if (end_ - at_ < 4) return false;
        out = 0;
        for (int i = 0; i < 4; ++i) {
            const char ch = at_[i];
            std::uint32_t digit;
            if (ch >= '0' && ch <= '9') digit = static_cast<std::uint32_t>(ch - '0');
            else if (ch >= 'a' && ch <= 'f') digit = static_cast<std::uint32_t>(ch - 'a' + 10);
            else if (ch >= 'A' && ch <= 'F') digit = static_cast<std::uint32_t>(ch - 'A' + 10);
            else return false;
            out = (out << 4) | digit;
        }
        at_ += 4;
        return true;
    }

    static void append_utf8(mstring& out, std::uint32_t point) {
        char bytes[4];
        std::size_t size;
        if (point < 0x80) {
            bytes[0] = static_cast<char>(point);
            size = 1;
        } else if (point < 0x800) {
            bytes[0] = static_cast<char>(0xC0 | (point >> 6));
            bytes[1] = static_cast<char>(0x80 | (point & 0x3F));
            size = 2;
        } else if (point < 0x10000) {
            bytes[0] = static_cast<char>(0xE0 | (point >> 12));
            bytes[1] = static_cast<char>(0x80 | ((point >> 6) & 0x3F));
            bytes[2] = static_cast<char>(0x80 | (point & 0x3F));
            size = 3;
        } else {
            bytes[0] = static_cast<char>(0xF0 | (point >> 18));
            bytes[1] = static_cast<char>(0x80 | ((point >> 12) & 0x3F));
            bytes[2] = static_cast<char>(0x80 | ((point >> 6) & 0x3F));
            bytes[3] = static_cast<char>(0x80 | (point & 0x3F));
            size = 4;
        }
        out.append(bytes, size);
    }

    bool parse_string(string& out) {
        ++at_;   // le guillemet ouvrant
        mstring text;
        while (true) {
            if (at_ == end_) return error("chaîne non terminée");
            const auto ch = static_cast<unsigned char>(*at_);
            if (ch == '"') {
                ++at_;
                out = text.to_immutable();
                return true;
            }
            if (ch < 0x20) return error("caractère de contrôle dans une chaîne");
            if (ch != '\\') {
                text.push_back(static_cast<char>(ch));
                ++at_;
                continue;
            }

            ++at_;
            if (at_ == end_) return error("chaîne non terminée");
            const char escape = *at_++;
            switch (escape) {
                case '"': text.push_back('"'); break;
                case '\\': text.push_back('\\'); break;
                case '/': text.push_back('/'); break;
                case 'b': text.push_back('\b'); break;
                case 'f': text.push_back('\f'); break;
                case 'n': text.push_back('\n'); break;
                case 'r': text.push_back('\r'); break;
                case 't': text.push_back('\t'); break;
                case 'u': {
                    std::uint32_t point;
                    if (!hex4(point)) return error("séquence \\u invalide");
                    if (point >= 0xD800 && point <= 0xDBFF) {
                        // Un substitut haut doit être suivi d'un substitut bas.
                        std::uint32_t low;
                        if (end_ - at_ < 2 || at_[0] != '\\' || at_[1] != 'u') return error("substitut UTF-16 isolé");
                        at_ += 2;
                        if (!hex4(low) || low < 0xDC00 || low > 0xDFFF) return error("substitut UTF-16 isolé");
                        point = 0x10000 + ((point - 0xD800) << 10) + (low - 0xDC00);
                    } else if (point >= 0xDC00 && point <= 0xDFFF) {
                        return error("substitut UTF-16 isolé");
                    }
                    append_utf8(text, point);
                    break;
                }
                default:
                    return error("échappement inconnu dans une chaîne");
            }
        }
    }

    bool parse_number(value& out) {
        const char* start = at_;
        bool negative = false;
        if (*at_ == '-') {
            negative = true;
            ++at_;
        }
        if (at_ == end_ || *at_ < '0' || *at_ > '9') return error("chiffre attendu");
        if (*at_ == '0') {
            ++at_;
        } else {
            while (at_ != end_ && *at_ >= '0' && *at_ <= '9') ++at_;
        }
        const char* digits_end = at_;

        bool is_real = false;
        if (at_ != end_ && *at_ == '.') {
            is_real = true;
            ++at_;
            if (at_ == end_ || *at_ < '0' || *at_ > '9') return error("chiffre attendu après le point");
            while (at_ != end_ && *at_ >= '0' && *at_ <= '9') ++at_;
        }
        if (at_ != end_ && (*at_ == 'e' || *at_ == 'E')) {
            is_real = true;
            ++at_;
            if (at_ != end_ && (*at_ == '+' || *at_ == '-')) ++at_;
            if (at_ == end_ || *at_ < '0' || *at_ > '9') return error("chiffre attendu dans l'exposant");
            while (at_ != end_ && *at_ >= '0' && *at_ <= '9') ++at_;
        }

        if (!is_real) {
            // Un entier : sa valeur absolue, tant qu'elle tient dans un uint64 (et l'int64 pour un négatif).
            std::uint64_t magnitude = 0;
            const char* first = start + (negative ? 1 : 0);
            const auto result = std::from_chars(first, digits_end, magnitude);
            const std::uint64_t negative_limit = static_cast<std::uint64_t>(std::numeric_limits<std::int64_t>::max()) + 1;
            if (result.ec == std::errc{} && (!negative || magnitude <= negative_limit)) {
                out.type = kind::integer;
                out.negative = negative && magnitude != 0;
                out.magnitude = magnitude;
                return true;
            }
        }

        // Un flottant, ou un entier trop grand pour l'être : un double.
        double number = 0.0;
        const auto result = std::from_chars(start, at_, number);
        if (result.ec == std::errc::result_out_of_range) return error("nombre hors des limites d'un double");
        if (result.ec != std::errc{}) return error("nombre invalide");
        out.type = kind::real;
        out.number = number;
        return true;
    }
};

}  // namespace

bool parse(const string& text, value& out, context& c) { return parser(text.data(), text.size(), c).run(out); }

}  // namespace rgl::standard::json
