// Tests du runtime : l'analyseur et l'écrivain XML (xml.hpp). Le programme renvoie 0 si tout passe, 1 sinon, et
// affiche chaque vérification ratée.

#include <cstdint>
#include <cstdio>
#include <string>

#include "rgl/standard/xml.hpp"

using rgl::standard::mstring;
using rgl::standard::string;
namespace xml = rgl::standard::xml;

namespace {

int failures = 0;

#define CHECK(condition)                                                                        \
    do {                                                                                        \
        if (!(condition)) {                                                                     \
            std::fprintf(stderr, "ÉCHEC %s:%d : %s\n", __FILE__, __LINE__, #condition);         \
            ++failures;                                                                         \
        }                                                                                       \
    } while (false)

bool parses(const char* text, xml::element& out) {
    out = xml::element{};
    xml::failure error;
    return xml::parse(string(text), out, error);
}

std::string error_of(const char* text) {
    xml::element out;
    xml::failure error;
    if (xml::parse(string(text), out, error)) return "";
    return std::string(error.message.data(), error.message.size());
}

void test_document() {
    xml::element root;
    CHECK(parses("<?xml version=\"1.0\"?>\n<!-- un commentaire -->\n<jeu nom=\"A &amp; B\" niveau='3'>\n"
                 "  <joueur id=\"1\">Léa</joueur>\n  <?pi ignorée?>\n  <vide/>\n  <joueur id=\"2\"><![CDATA[<x> & y]]></joueur>\n</jeu>\n", root));
    CHECK(root.name == string("jeu"));
    CHECK(root.attributes.size() == 2);
    CHECK(root.attributes[0].first == string("nom") && root.attributes[0].second == string("A & B"));
    CHECK(root.attributes[1].second == string("3"));
    CHECK(root.children.size() == 3);
    CHECK(root.text.size() == 0);     // des espaces seulement entre les fils
    CHECK(root.children[0].text == string("Léa"));
    CHECK(root.children[1].name == string("vide") && root.children[1].children.empty());
    CHECK(root.children[2].text == string("<x> & y"));
}

void test_entities_and_text() {
    xml::element root;
    CHECK(parses("<a>&lt;&gt;&amp;&quot;&apos;&#65;&#x1F600;</a>", root));
    CHECK(root.text == string("<>&\"'A\xF0\x9F\x98\x80"));
    CHECK(parses("<a>un <b/>deux</a>", root) && root.text == string("un deux") && root.children.size() == 1);
    CHECK(parses("<a>x\r\ny</a>", root) && root.text == string("x\ny"));
    CHECK(parses("<ns:a xmlns:ns=\"u\"><ns:b/></ns:a>", root) && root.name == string("ns:a") && root.children[0].name == string("ns:b"));
    CHECK(parses("<a x=\"a\tb\nc\"/>", root) && root.attributes[0].second == string("a b c"));
}

void test_errors() {
    CHECK(error_of("").find("vide") != std::string::npos);
    CHECK(error_of("<!DOCTYPE a><a/>").find("DOCTYPE") != std::string::npos);
    CHECK(error_of("<a>").find("non fermé") != std::string::npos);
    CHECK(error_of("<a></b>").find("différente") != std::string::npos);
    CHECK(error_of("<a x=\"1\" x=\"2\"/>").find("double") != std::string::npos);
    CHECK(error_of("<a>&nbsp;</a>").find("entité inconnue") != std::string::npos);
    CHECK(error_of("<a>&#0;</a>").find("invalide") != std::string::npos);
    CHECK(error_of("<a>&#x110000;</a>").find("Unicode") != std::string::npos);
    CHECK(error_of("<a>\n  <b>\n</a>").find("ligne 3") != std::string::npos);
    CHECK(error_of("<a/><b/>").find("après l'élément racine") != std::string::npos);
    CHECK(error_of("texte").find("« < »") != std::string::npos);
    CHECK(!error_of("<a x=1/>").empty());
    CHECK(!error_of("<a x=\"1/>").empty());
    CHECK(!error_of("<a><!-- non terminé</a>").empty());
    CHECK(!error_of("<a><![CDATA[x</a>").empty());
    CHECK(!error_of("<1a/>").empty());
    CHECK(!error_of("<a x=\"<\"/>").empty());
}

void test_depth() {
    std::string deep;
    for (int i = 0; i < 100000; ++i) deep += "<a>";
    xml::element root;
    xml::failure error;
    CHECK(!xml::parse(string(deep.c_str(), deep.size()), root, error));

    std::string fine;
    for (int i = 0; i < 200; ++i) fine += "<a>";
    for (int i = 0; i < 200; ++i) fine += "</a>";
    CHECK(xml::parse(string(fine.c_str(), fine.size()), root, error));
}

void test_serialize() {
    xml::element root;
    CHECK(parses("<jeu nom=\"A &amp; &quot;B&quot;\"><joueur id=\"1\">Léa &lt;3</joueur><vide/><groupe><x/></groupe></jeu>", root));

    mstring out;
    xml::failure error;
    CHECK(xml::serialize(root, out, error));
    CHECK(out.to_immutable() == string("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n"
                                       "<jeu nom=\"A &amp; &quot;B&quot;\">\n"
                                       "  <joueur id=\"1\">Léa &lt;3</joueur>\n"
                                       "  <vide/>\n"
                                       "  <groupe>\n"
                                       "    <x/>\n"
                                       "  </groupe>\n"
                                       "</jeu>"));

    // Ce qui s'écrit se relit pareil.
    xml::element again;
    const auto text = out.to_immutable();
    CHECK(xml::parse(text, again, error));
    CHECK(again.name == root.name && again.children.size() == 3 && again.children[0].text == string("Léa <3") && again.attributes[0].second == string("A & \"B\""));

    // Un nom qui n'en est pas un ne s'écrit pas.
    xml::element bad;
    bad.name = string("a b");
    mstring text2;
    CHECK(!xml::serialize(bad, text2, error));
    CHECK(error.message.size() > 0);
    xml::element ok;
    ok.name = string("a");
    ok.attributes.emplace_back(string("1x"), string("v"));
    mstring text3;
    CHECK(!xml::serialize(ok, text3, error));
}

// Des documents abîmés au hasard (graine fixe) : l'analyseur rend toujours vrai ou faux, sans planter ni boucler.
void test_hostile_documents() {
    const char* seeds[] = {
        "<?xml version=\"1.0\"?><a x=\"1&amp;2\" y='z'><b>t&#233;xt</b><!-- c --><c><![CDATA[d]]></c><d/></a>",
        "<jeu><joueur id=\"1\">A</joueur><joueur id=\"2\"><x><y/></x></joueur></jeu>",
    };
    std::uint64_t state = 0x9E3779B97F4A7C15ull;
    auto next = [&state]() {
        state ^= state << 13;
        state ^= state >> 7;
        state ^= state << 17;
        return state;
    };

    int accepted = 0;
    for (int round = 0; round < 30000; ++round) {
        std::string text = seeds[round % 2];
        const int edits = 1 + static_cast<int>(next() % 4);
        for (int e = 0; e < edits && !text.empty(); ++e) {
            const std::size_t at = next() % text.size();
            switch (next() % 4) {
                case 0: text[at] = static_cast<char>(next() & 0xFF); break;
                case 1: text.erase(at, 1 + next() % 3); break;
                case 2: text.insert(at, 1, "<>/&;\"'=![-?"[next() % 12]); break;
                default: text.resize(at); break;
            }
        }
        xml::element root;
        xml::failure error;
        if (xml::parse(string(text.c_str(), text.size()), root, error)) ++accepted;
        else CHECK(error.message.size() > 0);
    }
    CHECK(accepted > 0);
}

}  // namespace

int main() {
    test_document();
    test_entities_and_text();
    test_errors();
    test_depth();
    test_serialize();
    test_hostile_documents();
    return failures == 0 ? 0 : 1;
}
