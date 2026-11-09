// Composant « xml » du runtime : l'analyseur et l'écrivain de documents XML (xml.hpp).

#include "rgl/standard/xml.hpp"

#include <cstdint>
#include <string>
#include <utility>

#include "rgl/standard/format.hpp"

namespace rgl::standard::xml {

namespace {

bool is_name_start(unsigned char c) { return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_' || c == ':' || c >= 0x80; }
bool is_name_part(unsigned char c) { return is_name_start(c) || (c >= '0' && c <= '9') || c == '-' || c == '.'; }
bool is_space(char c) { return c == ' ' || c == '\t' || c == '\n' || c == '\r'; }

void append_utf8(mstring& out, std::uint32_t point) {
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

class parser {
   public:
    parser(const char* data, std::size_t size, failure& error) : begin_(data), at_(data), end_(data + size), error_(error) {}

    bool run(element& root) {
        // Le BOM est déjà retiré par la lecture du fichier.
        if (!skip_misc(true)) return false;
        if (at_ == end_) return fail("document vide");
        if (*at_ != '<') return fail("« < » attendu : un document XML commence par un élément");
        if (!parse_element(root, 0)) return false;
        if (!skip_misc(false)) return false;
        if (at_ != end_) return fail("texte inattendu après l'élément racine");
        return true;
    }

   private:
    const char* begin_;
    const char* at_;
    const char* end_;
    failure& error_;

    bool fail(const char* what) {
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
        error_.message = interpolate(string("XML invalide, ligne "), line, string(", colonne "), column, string(" : "), string(what));
        return false;
    }

    bool starts_with(const char* word) const {
        std::size_t size = std::char_traits<char>::length(word);
        return static_cast<std::size_t>(end_ - at_) >= size && std::char_traits<char>::compare(at_, word, size) == 0;
    }

    void skip_space() {
        while (at_ != end_ && is_space(*at_)) ++at_;
    }

    // Saute jusqu'après `terminator` ; faux s'il manque.
    bool skip_until(const char* terminator, const char* what) {
        const std::size_t size = std::char_traits<char>::length(terminator);
        while (static_cast<std::size_t>(end_ - at_) >= size) {
            if (std::char_traits<char>::compare(at_, terminator, size) == 0) {
                at_ += size;
                return true;
            }
            ++at_;
        }
        at_ = end_;
        return fail(what);
    }

    // Les espaces, commentaires et instructions de traitement (dont la déclaration `<?xml ?>`) autour de l'élément racine.
    bool skip_misc(bool before) {
        while (true) {
            skip_space();
            if (starts_with("<!--")) {
                at_ += 4;
                if (!skip_until("-->", "commentaire non terminé")) return false;
            } else if (starts_with("<?")) {
                at_ += 2;
                if (!skip_until("?>", "instruction de traitement non terminée")) return false;
            } else if (before && starts_with("<!DOCTYPE")) {
                return fail("une balise DOCTYPE n'est pas prise en charge");
            } else {
                return true;
            }
        }
    }

    bool parse_name(string& out) {
        if (at_ == end_ || !is_name_start(static_cast<unsigned char>(*at_))) return fail("nom attendu");
        const char* start = at_;
        while (at_ != end_ && is_name_part(static_cast<unsigned char>(*at_))) ++at_;
        out = string(start, static_cast<std::size_t>(at_ - start));
        return true;
    }

    // Une entité après « & » : les cinq prédéfinies et les références numériques.
    bool parse_entity(mstring& out) {
        ++at_;   // le &
        const char* start = at_;
        while (at_ != end_ && *at_ != ';' && at_ - start < 12) ++at_;
        if (at_ == end_ || *at_ != ';') return fail("entité non terminée");
        const std::size_t size = static_cast<std::size_t>(at_ - start);
        ++at_;

        auto is = [&](const char* name) { return std::char_traits<char>::length(name) == size && std::char_traits<char>::compare(start, name, size) == 0; };
        if (is("lt")) out.push_back('<');
        else if (is("gt")) out.push_back('>');
        else if (is("amp")) out.push_back('&');
        else if (is("quot")) out.push_back('"');
        else if (is("apos")) out.push_back('\'');
        else if (size >= 2 && start[0] == '#') {
            std::uint32_t point = 0;
            const bool hex = start[1] == 'x';
            std::size_t i = hex ? 2 : 1;
            if (i == size) return fail("référence numérique vide");
            for (; i < size; ++i) {
                const char c = start[i];
                std::uint32_t digit;
                if (c >= '0' && c <= '9') digit = static_cast<std::uint32_t>(c - '0');
                else if (hex && c >= 'a' && c <= 'f') digit = static_cast<std::uint32_t>(c - 'a' + 10);
                else if (hex && c >= 'A' && c <= 'F') digit = static_cast<std::uint32_t>(c - 'A' + 10);
                else return fail("référence numérique invalide");
                point = point * (hex ? 16 : 10) + digit;
                if (point > 0x10FFFF) return fail("référence numérique hors de l'Unicode");
            }
            if (point == 0 || (point >= 0xD800 && point <= 0xDFFF)) return fail("référence numérique invalide");
            append_utf8(out, point);
        } else {
            return fail("entité inconnue : seules &lt; &gt; &amp; &quot; &apos; et les références numériques sont prises en charge");
        }
        return true;
    }

    bool parse_attribute_value(string& out) {
        if (at_ == end_ || (*at_ != '"' && *at_ != '\'')) return fail("valeur d'attribut entre guillemets attendue");
        const char quote = *at_++;
        mstring value;
        while (true) {
            if (at_ == end_) return fail("valeur d'attribut non terminée");
            const char c = *at_;
            if (c == quote) {
                ++at_;
                out = value.to_immutable();
                return true;
            }
            if (c == '<') return fail("« < » interdit dans une valeur d'attribut");
            if (c == '&') {
                if (!parse_entity(value)) return false;
                continue;
            }
            // Un saut de ligne ou une tabulation d'une valeur d'attribut vaut une espace.
            value.push_back(c == '\n' || c == '\r' || c == '\t' ? ' ' : c);
            ++at_;
        }
    }

    bool parse_element(element& out, int depth) {
        if (depth > max_depth) return fail("document trop profond");
        ++at_;   // le <
        if (!parse_name(out.name)) return false;

        while (true) {
            skip_space();
            if (at_ == end_) return fail("balise non terminée");
            if (*at_ == '/' || *at_ == '>') break;

            string key;
            if (!parse_name(key)) return false;
            skip_space();
            if (at_ == end_ || *at_ != '=') return fail("« = » attendu après le nom de l'attribut");
            ++at_;
            skip_space();
            string value;
            if (!parse_attribute_value(value)) return false;
            for (const auto& existing : out.attributes) {
                if (existing.first == key) return fail("attribut en double");
            }
            out.attributes.emplace_back(static_cast<string&&>(key), static_cast<string&&>(value));
        }

        if (*at_ == '/') {
            ++at_;
            if (at_ == end_ || *at_ != '>') return fail("« > » attendu après « / »");
            ++at_;
            return true;
        }
        ++at_;   // le >

        mstring text;
        while (true) {
            if (at_ == end_) return fail("élément non fermé");
            if (starts_with("</")) {
                at_ += 2;
                string closing;
                if (!parse_name(closing)) return false;
                skip_space();
                if (at_ == end_ || *at_ != '>') return fail("« > » attendu dans la balise fermante");
                if (!(closing == out.name)) return fail("balise fermante différente de la balise ouvrante");
                ++at_;
                break;
            }
            if (starts_with("<!--")) {
                at_ += 4;
                if (!skip_until("-->", "commentaire non terminé")) return false;
            } else if (starts_with("<![CDATA[")) {
                at_ += 9;
                const char* start = at_;
                if (!skip_until("]]>", "section CDATA non terminée")) return false;
                text.append(start, static_cast<std::size_t>(at_ - 3 - start));
            } else if (starts_with("<?")) {
                at_ += 2;
                if (!skip_until("?>", "instruction de traitement non terminée")) return false;
            } else if (starts_with("<!")) {
                return fail("déclaration inattendue");
            } else if (*at_ == '<') {
                element child;
                if (!parse_element(child, depth + 1)) return false;
                out.children.push_back(static_cast<element&&>(child));
            } else if (*at_ == '&') {
                if (!parse_entity(text)) return false;
            } else {
                // Les fins de ligne se normalisent en « \n », comme le veut XML.
                if (*at_ == '\r') {
                    text.push_back('\n');
                    ++at_;
                    if (at_ != end_ && *at_ == '\n') ++at_;
                } else {
                    text.push_back(*at_++);
                }
            }
        }

        // Du texte fait seulement d'espaces entre des fils n'est pas du texte.
        bool blank = true;
        for (std::size_t i = 0; i < text.size(); ++i) {
            if (!is_space(text.data()[i])) {
                blank = false;
                break;
            }
        }
        if (!blank) out.text = text.to_immutable();
        return true;
    }
};

bool valid_name(const string& name) {
    if (name.size() == 0 || !is_name_start(static_cast<unsigned char>(name.data()[0]))) return false;
    for (std::size_t i = 1; i < name.size(); ++i) {
        if (!is_name_part(static_cast<unsigned char>(name.data()[i]))) return false;
    }
    return true;
}

// Du texte, ou une valeur d'attribut (`attribute` : guillemets échappés aussi), avec ses caractères réservés en entités.
void escape(mstring& out, const string& text, bool attribute) {
    for (std::size_t i = 0; i < text.size(); ++i) {
        const char c = text.data()[i];
        switch (c) {
            case '<': out.append("&lt;", 4); break;
            case '>': out.append("&gt;", 4); break;
            case '&': out.append("&amp;", 5); break;
            case '"':
                if (attribute) out.append("&quot;", 6);
                else out.push_back(c);
                break;
            case '\n':
                if (attribute) out.append("&#10;", 5);
                else out.push_back(c);
                break;
            case '\t':
                if (attribute) out.append("&#9;", 4);
                else out.push_back(c);
                break;
            case '\r': out.append("&#13;", 5); break;
            default: out.push_back(c);
        }
    }
}

void indent(mstring& out, int depth) {
    for (int i = 0; i < depth; ++i) out.append("  ", 2);
}

bool write_element(mstring& out, const element& node, int depth, failure& error) {
    if (depth > max_depth) {
        error.message = string("document trop profond");
        return false;
    }
    if (!valid_name(node.name)) {
        error.message = interpolate(string("« "), node.name, string(" » n'est pas un nom de balise XML"));
        return false;
    }

    indent(out, depth);
    out.push_back('<');
    out.append(node.name.data(), node.name.size());
    for (const auto& attribute : node.attributes) {
        if (!valid_name(attribute.first)) {
            error.message = interpolate(string("« "), attribute.first, string(" » n'est pas un nom d'attribut XML"));
            return false;
        }
        out.push_back(' ');
        out.append(attribute.first.data(), attribute.first.size());
        out.append("=\"", 2);
        escape(out, attribute.second, true);
        out.push_back('"');
    }

    if (node.children.empty() && node.text.size() == 0) {
        out.append("/>", 2);
        return true;
    }
    out.push_back('>');
    if (node.children.empty()) {
        escape(out, node.text, false);
    } else {
        out.push_back('\n');
        if (node.text.size() != 0) {
            indent(out, depth + 1);
            escape(out, node.text, false);
            out.push_back('\n');
        }
        for (const auto& child : node.children) {
            if (!write_element(out, child, depth + 1, error)) return false;
            out.push_back('\n');
        }
        indent(out, depth);
    }
    out.append("</", 2);
    out.append(node.name.data(), node.name.size());
    out.push_back('>');
    return true;
}

}  // namespace

bool parse(const string& text, element& root, failure& error) { return parser(text.data(), text.size(), error).run(root); }

bool serialize(const element& root, mstring& out, failure& error) {
    out.append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
    return write_element(out, root, 0, error);
}

}  // namespace rgl::standard::xml
