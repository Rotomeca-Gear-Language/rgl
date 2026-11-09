// Tests du runtime : la normalisation Unicode (unicode.hpp). Le programme renvoie 0 si tout passe, 1 sinon. Il lit aussi
// `NormalizationTest.txt` (le fichier de conformité du consortium Unicode), dont le chemin est son premier argument.

#include <cstdio>
#include <cstdlib>
#include <fstream>
#include <sstream>
#include <string>
#include <vector>

#include "rgl/standard/unicode.hpp"

using rgl::standard::string;
namespace unicode = rgl::standard::unicode;

namespace {

int failures = 0;

#define CHECK(condition)                                                                        \
    do {                                                                                        \
        if (!(condition)) {                                                                     \
            std::fprintf(stderr, "ÉCHEC %s:%d : %s\n", __FILE__, __LINE__, #condition);         \
            ++failures;                                                                         \
        }                                                                                       \
    } while (false)

std::string norm(const std::string& text, unicode::form which) {
    const string result = unicode::normalize(string(text.data(), text.size()), which);
    return std::string(result.data(), result.size());
}

void append_utf8(std::string& out, std::uint32_t code) {
    if (code < 0x80) {
        out.push_back(static_cast<char>(code));
    } else if (code < 0x800) {
        out.push_back(static_cast<char>(0xC0 | (code >> 6)));
        out.push_back(static_cast<char>(0x80 | (code & 0x3F)));
    } else if (code < 0x10000) {
        out.push_back(static_cast<char>(0xE0 | (code >> 12)));
        out.push_back(static_cast<char>(0x80 | ((code >> 6) & 0x3F)));
        out.push_back(static_cast<char>(0x80 | (code & 0x3F)));
    } else {
        out.push_back(static_cast<char>(0xF0 | (code >> 18)));
        out.push_back(static_cast<char>(0x80 | ((code >> 12) & 0x3F)));
        out.push_back(static_cast<char>(0x80 | ((code >> 6) & 0x3F)));
        out.push_back(static_cast<char>(0x80 | (code & 0x3F)));
    }
}

std::string from_hex_list(const std::string& field) {
    std::istringstream stream(field);
    std::string word;
    std::string out;
    while (stream >> word) append_utf8(out, static_cast<std::uint32_t>(std::strtoul(word.c_str(), nullptr, 16)));
    return out;
}

std::vector<std::string> split(const std::string& line, char separator) {
    std::vector<std::string> parts;
    std::string current;
    for (const char c : line) {
        if (c == separator) {
            parts.push_back(current);
            current.clear();
        } else {
            current.push_back(c);
        }
    }
    parts.push_back(current);
    return parts;
}

void examples() {
    const std::string e_acute = "\xC3\xA9";                // é
    const std::string e_accent = "e\xCC\x81";              // e + accent aigu
    CHECK(norm(e_accent, unicode::form::nfc) == e_acute);
    CHECK(norm(e_acute, unicode::form::nfd) == e_accent);
    CHECK(norm(e_acute, unicode::form::nfc) == e_acute);
    CHECK(norm("abc", unicode::form::nfc) == "abc");
    CHECK(norm("", unicode::form::nfd).empty());

    // Compatibilité : « ﬁ » (U+FB01) devient « fi », « ① » devient « 1 ».
    CHECK(norm("\xEF\xAC\x81", unicode::form::nfkc) == "fi");
    CHECK(norm("\xEF\xAC\x81", unicode::form::nfc) == "\xEF\xAC\x81");
    CHECK(norm("\xE2\x91\xA0", unicode::form::nfkd) == "1");

    // Hangul : la syllabe « 한 » (U+D55C) se décompose en trois jamos et se recompose.
    const std::string han = "\xED\x95\x9C";
    const std::string jamos = "\xE1\x84\x92\xE1\x85\xA1\xE1\x86\xAB";
    CHECK(norm(han, unicode::form::nfd) == jamos);
    CHECK(norm(jamos, unicode::form::nfc) == han);

    // Ordre canonique : deux accents de classes différentes se rangent, quelle que soit leur ordre d'écriture.
    CHECK(norm("a\xCC\x81\xCC\xA3", unicode::form::nfd) == norm("a\xCC\xA3\xCC\x81", unicode::form::nfd));

    // Un octet invalide reste en place.
    CHECK(norm("e\xCC\x81\xFFx", unicode::form::nfc) == e_acute + "\xFF" + "x");

    CHECK(unicode::combining_class(0x301) == 230);
    CHECK(unicode::combining_class(0x323) == 220);
    CHECK(unicode::combining_class('a') == 0);

    const string stripped = unicode::strip_marks(string("H\xC3\xA9ros \xC3\xA0 l'\xC3\xA9" "cole"));
    CHECK(std::string(stripped.data(), stripped.size()) == "Heros a l'ecole");
}

void conformance(const char* path) {
    std::ifstream file(path);
    if (!file) {
        std::fprintf(stderr, "ÉCHEC : %s introuvable\n", path);
        ++failures;
        return;
    }
    std::string line;
    int checked = 0;
    while (std::getline(file, line)) {
        if (line.empty() || line[0] == '#' || line[0] == '@') continue;
        const std::vector<std::string> fields = split(line, ';');
        if (fields.size() < 5) continue;
        std::string c[5];
        for (int i = 0; i < 5; ++i) c[i] = from_hex_list(fields[static_cast<std::size_t>(i)]);
        ++checked;
        const auto expect = [&](const std::string& got, const std::string& want, const char* what) {
            if (got != want) {
                std::fprintf(stderr, "ÉCHEC conformité (%s) ligne %d : %s\n", what, checked, line.substr(0, 60).c_str());
                ++failures;
            }
        };
        for (int i : {0, 1, 2}) expect(norm(c[i], unicode::form::nfc), c[1], "NFC");
        for (int i : {3, 4}) expect(norm(c[i], unicode::form::nfc), c[3], "NFC");
        for (int i : {0, 1, 2}) expect(norm(c[i], unicode::form::nfd), c[2], "NFD");
        for (int i : {3, 4}) expect(norm(c[i], unicode::form::nfd), c[4], "NFD");
        for (int i = 0; i < 5; ++i) expect(norm(c[i], unicode::form::nfkc), c[3], "NFKC");
        for (int i = 0; i < 5; ++i) expect(norm(c[i], unicode::form::nfkd), c[4], "NFKD");
        if (failures > 20) return;
    }
    CHECK(checked > 15000);
}

}  // namespace

int main(int argc, char** argv) {
    examples();
    conformance(argc > 1 ? argv[1] : "NormalizationTest.txt");
    if (failures == 0) std::printf("unicode_tests : tout passe\n");
    return failures == 0 ? 0 : 1;
}
