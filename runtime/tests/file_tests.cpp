// Tests du runtime : lecture et écriture de fichiers entiers (file.hpp). Le programme reçoit en
// argument un dossier où écrire (vide au départ) ; il renvoie 0 si tout passe, 1 sinon, et affiche
// chaque vérification ratée.

#include <cstdint>
#include <cstdio>
#include <memory>

#include "rgl/standard/file.hpp"

using rgl::standard::array;
using rgl::standard::string;
namespace file = rgl::standard::file;

namespace {

int failures = 0;

#define CHECK(condition)                                                                        \
    do {                                                                                        \
        if (!(condition)) {                                                                     \
            std::fprintf(stderr, "ÉCHEC %s:%d : %s\n", __FILE__, __LINE__, #condition);         \
            ++failures;                                                                         \
        }                                                                                       \
    } while (false)

string directory;

string path_of(const char* name) { return directory + string("/") + string(name); }

void test_utf8() {
    CHECK(rgl::standard::bytes::is_valid_utf8("", 0));
    CHECK(rgl::standard::bytes::is_valid_utf8("abc", 3));
    CHECK(rgl::standard::bytes::is_valid_utf8("\xC3\xA9t\xC3\xA9", 5));           // été
    CHECK(rgl::standard::bytes::is_valid_utf8("\xE2\x9C\x93", 3));                // ✓
    CHECK(rgl::standard::bytes::is_valid_utf8("\xF0\x9F\x98\x80", 4));            // U+1F600
    CHECK(!rgl::standard::bytes::is_valid_utf8("\xC3", 1));                       // séquence coupée
    CHECK(!rgl::standard::bytes::is_valid_utf8("\x80", 1));                       // octet de continuation seul
    CHECK(!rgl::standard::bytes::is_valid_utf8("\xC0\xAF", 2));                   // trop longue
    CHECK(!rgl::standard::bytes::is_valid_utf8("\xE0\x80\xAF", 3));               // trop longue
    CHECK(!rgl::standard::bytes::is_valid_utf8("\xED\xA0\x80", 3));               // substitut UTF-16
    CHECK(!rgl::standard::bytes::is_valid_utf8("\xF4\x90\x80\x80", 4));           // au-delà de U+10FFFF
    CHECK(!rgl::standard::bytes::is_valid_utf8("\xFF", 1));
}

void test_conversions() {
    using rgl::standard::bytes::from_string;
    using rgl::standard::bytes::try_to_string;

    const auto octets = from_string(string("été"));
    CHECK(octets.size() == 5);
    CHECK(octets[0] == 0xC3 && octets[1] == 0xA9 && octets[2] == 't');
    CHECK(try_to_string(octets) == string("été"));

    array<std::uint8_t> invalide{0xC3, 0x28};
    CHECK(!try_to_string(invalide).has_value());
    CHECK(try_to_string(array<std::uint8_t>{}) == string());
    CHECK(rgl::standard::bytes::is_text(rgl::standard::fixed_array<std::uint8_t, 2>{104, 105}));
}

void test_streams() {
    using rgl::standard::nullable;
    const string path = path_of("flux.txt");
    CHECK(file::write_text(path, string("un\r\ndeux\n\nétoile")) == file::status::ok);

    nullable<string> line;
    file::stream never_opened;
    CHECK(never_opened.read_line(line) == file::status::closed);   // jamais ouvert

    file::stream in;
    CHECK(file::open(path, file::open_mode::read, in) == file::status::ok);
    CHECK(in.is_open() && in.path() == path);

    CHECK(in.read_line(line) == file::status::ok && line && *line == string("un"));
    CHECK(in.read_line(line) == file::status::ok && line && *line == string("deux"));
    CHECK(in.read_line(line) == file::status::ok && line && line->size() == 0);            // ligne vide
    CHECK(in.read_line(line) == file::status::ok && line && *line == string("étoile"));    // dernière ligne sans retour
    CHECK(in.read_line(line) == file::status::ok && !line.has_value());                    // fin
    CHECK(in.read_line(line) == file::status::ok && !line.has_value());

    // Lire des octets, puis fermer : toutes les copies sont fermées.
    file::stream bytes_in;
    CHECK(file::open(path, file::open_mode::read, bytes_in) == file::status::ok);
    file::stream copy = bytes_in;
    array<std::uint8_t> chunk;
    CHECK(bytes_in.read_bytes(4, chunk) == file::status::ok && chunk.size() == 4 && chunk[0] == 'u' && chunk[2] == '\r');
    CHECK(copy.read_line(line) == file::status::ok && line && *line == string("deux"));     // la copie partage la position
    CHECK(bytes_in.read_bytes(1000, chunk) == file::status::ok && chunk.size() == 8);        // le reste : « \n étoile » en octets
    CHECK(bytes_in.read_bytes(10, chunk) == file::status::ok && chunk.size() == 0);
    bytes_in.close();
    CHECK(!copy.is_open());
    CHECK(copy.read_bytes(1, chunk) == file::status::closed);
    CHECK(bytes_in == copy);

    // Écrire : remplacer, puis ajouter.
    const string out_path = path_of("sortie.txt");
    {
        file::stream out;
        CHECK(file::open(out_path, file::open_mode::write, out) == file::status::ok);
        CHECK(out.write_text(string("a\n")) == file::status::ok);
        CHECK(out.write_bytes(array<std::uint8_t>{'b', '\n'}) == file::status::ok);
        CHECK(out.read_line(line) == file::status::wrong_mode);
        // Le flux se ferme ici, à la fin de sa portée.
    }
    {
        file::stream out;
        CHECK(file::open(out_path, file::open_mode::append, out) == file::status::ok);
        CHECK(out.write_text(string("c")) == file::status::ok);
    }
    string whole;
    CHECK(file::read_text(out_path, whole) == file::status::ok && whole == string("a\nb\nc"));
    CHECK(in.write_text(string("x")) == file::status::wrong_mode);                           // un flux en lecture n'écrit pas

    // Les erreurs d'ouverture.
    file::stream missing;
    CHECK(file::open(path_of("absent.txt"), file::open_mode::read, missing) == file::status::not_found);
    CHECK(!missing.is_open());
    CHECK(file::open(path_of("dossier/x.txt"), file::open_mode::write, missing) == file::status::not_found);

    // Des lignes qui s'étendent sur plusieurs blocs de lecture.
    rgl::standard::mstring big;
    for (int i = 0; i < 20000; ++i) big.append("ligne numéro 12345\n");
    CHECK(file::write_text(path_of("gros.txt"), big.to_immutable()) == file::status::ok);
    file::stream many;
    CHECK(file::open(path_of("gros.txt"), file::open_mode::read, many) == file::status::ok);
    int lines = 0;
    while (many.read_line(line) == file::status::ok && line) {
        CHECK(*line == string("ligne numéro 12345"));
        ++lines;
    }
    CHECK(lines == 20000);

    // Du texte qui n'est pas de l'UTF-8 ne se lit pas en lignes, mais se lit en octets.
    CHECK(file::write(path_of("mauvais.bin"), "ab\xC3\x28\n", 5) == file::status::ok);
    file::stream bad;
    CHECK(file::open(path_of("mauvais.bin"), file::open_mode::read, bad) == file::status::ok);
    CHECK(bad.read_line(line) == file::status::invalid_utf8);
}

void test_text() {
    const string path = path_of("texte.txt");
    CHECK(file::write_text(path, string("été\r\nligne 2\n")) == file::status::ok);

    string text("avant");
    CHECK(file::read_text(path, text) == file::status::ok);
    CHECK(text == string("été\r\nligne 2\n"));   // aucun octet modifié, retours à la ligne compris

    // Un fichier vide donne un texte vide.
    CHECK(file::write_text(path, string()) == file::status::ok);
    CHECK(file::read_text(path, text) == file::status::ok);
    CHECK(text.size() == 0);

    // Un fichier plus long qu'un bloc de lecture.
    rgl::standard::mstring big;
    for (int i = 0; i < 100000; ++i) big.append("é1", 3);
    CHECK(file::write_text(path, big.to_immutable()) == file::status::ok);
    CHECK(file::read_text(path, text) == file::status::ok);
    CHECK(text.size() == 300000);
}

void test_bom() {
    const string path = path_of("bom.txt");
    CHECK(file::write_text(path, string("\xEF\xBB\xBF" "abc")) == file::status::ok);

    string text;
    CHECK(file::read_text(path, text) == file::status::ok);
    CHECK(text == string("abc"));   // le BOM est retiré du texte

    array<std::uint8_t> bytes;
    CHECK(file::read_bytes(path, bytes) == file::status::ok);
    CHECK(bytes.size() == 6);        // mais gardé dans les octets
    CHECK(bytes[0] == 0xEF && bytes[3] == 'a');
}

void test_bytes() {
    const string path = path_of("octets.bin");
    array<std::uint8_t> data{0, 1, 2, 255, 0, 10, 13};
    CHECK(file::write_bytes(path, data) == file::status::ok);

    array<std::uint8_t> read;
    CHECK(file::read_bytes(path, read) == file::status::ok);
    CHECK(read.size() == 7);
    for (std::size_t i = 0; i < 7; ++i) CHECK(read[i] == data[i]);
}

void test_errors() {
    string text("inchangé");
    CHECK(file::read_text(path_of("absent.txt"), text) == file::status::not_found);
    CHECK(text == string("inchangé"));
    CHECK(file::read_text(path_of("dossier/absent.txt"), text) == file::status::not_found);
    CHECK(file::write_text(path_of("dossier/absent.txt"), string("x")) == file::status::not_found);

    // Un dossier n'est pas un fichier.
    CHECK(file::read_text(directory, text) != file::status::ok);
    CHECK(file::read_text(string(), text) == file::status::failed);

    // Des octets qui ne sont pas de l'UTF-8 se lisent en octets, jamais en texte.
    const string path = path_of("invalide.bin");
    CHECK(file::write(path, "\xC3\x28", 2) == file::status::ok);
    CHECK(file::read_text(path, text) == file::status::invalid_utf8);
    CHECK(text == string("inchangé"));
    array<std::uint8_t> bytes;
    CHECK(file::read_bytes(path, bytes) == file::status::ok);
    CHECK(bytes.size() == 2);

    CHECK(file::message(file::status::not_found) == string("fichier introuvable"));
}

}  // namespace

int main(int argc, char** argv) {
    if (argc < 2) {
        std::fprintf(stderr, "dossier de travail attendu\n");
        return 2;
    }
    directory = string(argv[1]);

    test_utf8();
    test_conversions();
    test_text();
    test_streams();
    test_bom();
    test_bytes();
    test_errors();
    return failures == 0 ? 0 : 1;
}
