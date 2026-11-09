// Tests du runtime : l'analyseur JSON, les lecteurs de valeurs et les écrivains (json.hpp). Le programme
// renvoie 0 si tout passe, 1 sinon, et affiche chaque vérification ratée.

#include <cstdint>
#include <cstdio>
#include <string>

#include "rgl/standard/json.hpp"

using rgl::standard::mstring;
using rgl::standard::nullable;
using rgl::standard::string;
namespace json = rgl::standard::json;

namespace {

int failures = 0;

#define CHECK(condition)                                                                        \
    do {                                                                                        \
        if (!(condition)) {                                                                     \
            std::fprintf(stderr, "ÉCHEC %s:%d : %s\n", __FILE__, __LINE__, #condition);         \
            ++failures;                                                                         \
        }                                                                                       \
    } while (false)

bool parses(const char* text, json::value& out) {
    out = json::value{};
    json::context c;
    return json::parse(string(text), out, c);
}

std::string error_of(const char* text) {
    json::value v;
    json::context c;
    if (json::parse(string(text), v, c)) return "";
    return std::string(c.message().data(), c.message().size());
}

void test_values() {
    json::value v;
    CHECK(parses("{\"a\": 1, \"b\": [true, null, \"x\"], \"c\": {\"d\": -2.5}}", v));
    CHECK(v.type == json::kind::object);
    CHECK(v.keys.size() == 3);
    CHECK(json::member(v, "a") != nullptr && json::member(v, "a")->magnitude == 1);
    CHECK(json::member(v, "b")->items.size() == 3);
    CHECK(json::member(v, "b")->items[0].flag);
    CHECK(json::member(v, "b")->items[1].type == json::kind::null);
    CHECK(json::member(v, "c")->items[0].number == -2.5);
    CHECK(json::member(v, "absent") == nullptr);

    // Le dernier membre de même nom l'emporte.
    CHECK(parses("{\"a\": 1, \"a\": 2}", v));
    CHECK(json::member(v, "a")->magnitude == 2);

    CHECK(parses("  [ ]  ", v) && v.type == json::kind::array && v.items.empty());
    CHECK(parses("{}", v) && v.type == json::kind::object);
}

void test_numbers() {
    json::value v;
    CHECK(parses("0", v) && v.type == json::kind::integer && v.magnitude == 0 && !v.negative);
    CHECK(parses("-0", v) && v.type == json::kind::integer && !v.negative);
    CHECK(parses("18446744073709551615", v) && v.type == json::kind::integer && v.magnitude == 18446744073709551615ull);
    CHECK(parses("18446744073709551616", v) && v.type == json::kind::real);        // trop grand : un double
    CHECK(parses("-9223372036854775808", v) && v.type == json::kind::integer && v.negative);
    CHECK(parses("-9223372036854775809", v) && v.type == json::kind::real);
    CHECK(parses("1e3", v) && v.type == json::kind::real && v.number == 1000.0);
    CHECK(parses("3.0", v) && v.type == json::kind::real);

    nullable<std::int8_t> small;
    json::context c;
    CHECK(parses("127", v) && json::read_integer(v, small, c, "byte") && *small == 127);
    CHECK(parses("128", v) && !json::read_integer(v, small, c, "byte"));
    CHECK(parses("-128", v) && json::read_integer(v, small, c, "byte") && *small == -128);
    CHECK(parses("-129", v) && !json::read_integer(v, small, c, "byte"));

    nullable<std::uint32_t> unsigned_value;
    CHECK(parses("-1", v) && !json::read_integer(v, unsigned_value, c, "uint"));
    CHECK(parses("4294967295", v) && json::read_integer(v, unsigned_value, c, "uint") && *unsigned_value == 4294967295u);

    nullable<std::int64_t> big;
    CHECK(parses("-9223372036854775808", v) && json::read_integer(v, big, c, "long") && *big == INT64_MIN);
    CHECK(parses("3.5", v) && !json::read_integer(v, big, c, "long"));     // un flottant n'est pas un entier

    nullable<double> real;
    CHECK(parses("3", v) && json::read_real(v, real, c, "double") && *real == 3.0);     // un entier est aussi un double
    CHECK(parses("3.25", v) && json::read_real(v, real, c, "double") && *real == 3.25);
    CHECK(parses("\"x\"", v) && !json::read_real(v, real, c, "double"));
}

void test_strings() {
    json::value v;
    CHECK(parses("\"a\\n\\t\\\"\\\\\\/\"", v) && v.text == string("a\n\t\"\\/"));
    CHECK(parses("\"\\u00e9\\u2713\"", v) && v.text == string("é✓"));
    CHECK(parses("\"\\ud83d\\ude00\"", v) && v.text == string("\xF0\x9F\x98\x80"));   // paire de substituts
    CHECK(parses("\"été\"", v) && v.text == string("été"));

    CHECK(error_of("\"\\ud83d\"").find("substitut") != std::string::npos);
    CHECK(error_of("\"\\ude00\"").find("substitut") != std::string::npos);
    CHECK(error_of("\"\\q\"").find("échappement") != std::string::npos);
    CHECK(error_of("\"abc").find("non terminée") != std::string::npos);
    CHECK(error_of("\"a\nb\"").find("contrôle") != std::string::npos);
}

void test_errors() {
    CHECK(error_of("").find("vide") != std::string::npos);
    CHECK(error_of("{\"a\": }").find("ligne 1, colonne 7") != std::string::npos);
    CHECK(error_of("[1,\n 2,\n x]").find("ligne 3, colonne 2") != std::string::npos);
    CHECK(!error_of("[1 2]").empty());
    CHECK(!error_of("{\"a\" 1}").empty());
    CHECK(!error_of("{a: 1}").empty());
    CHECK(!error_of("[1,]").empty());
    CHECK(!error_of("01").empty());
    CHECK(!error_of("1.").empty());
    CHECK(!error_of("1e").empty());
    CHECK(!error_of("-").empty());
    CHECK(!error_of("tru").empty());
    CHECK(!error_of("[1] x").empty());
    CHECK(!error_of("{\"a\": 1").empty());
    CHECK(!error_of("[").empty());
}

void test_depth() {
    // Un document trop profond est refusé sans épuiser la pile.
    std::string deep(100000, '[');
    json::value v;
    json::context c;
    CHECK(!json::parse(string(deep.c_str(), deep.size()), v, c));
    CHECK(c.message().size() > 0);

    std::string fine(200, '[');
    fine += std::string(200, ']');
    CHECK(json::parse(string(fine.c_str(), fine.size()), v, c));
}

// Des documents abîmés au hasard (graine fixe, donc reproductible) : l'analyseur rend toujours vrai ou faux, sans
// jamais planter ni boucler,.
void test_hostile_documents() {
    const char* seeds[] = {
        "{\"a\": [1, 2.5e3, -0, true, null, \"x\\u00e9\\ud83d\\ude00\"], \"b\": {\"c\": {}}}",
        "[[[[]]], {\"k\": [{}, []]}, \"\", 18446744073709551615, -9223372036854775808]",
        "{\"nom\": \"é\", \"v\": 1e400, \"w\": 123456789012345678901234567890}",
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
        std::string text = seeds[round % 3];
        const int edits = 1 + static_cast<int>(next() % 4);
        for (int e = 0; e < edits && !text.empty(); ++e) {
            const std::size_t at = next() % text.size();
            switch (next() % 4) {
                case 0: text[at] = static_cast<char>(next() & 0xFF); break;
                case 1: text.erase(at, 1 + next() % 3); break;
                case 2: text.insert(at, 1, "{}[]\",:\\-0e."[next() % 12]); break;
                default: text.resize(at); break;
            }
        }
        json::value v;
        json::context c;
        if (json::parse(string(text.c_str(), text.size()), v, c)) ++accepted;
        else CHECK(c.message().size() > 0);
    }
    CHECK(accepted > 0);
}

void test_path() {
    json::value v;
    json::context c;
    CHECK(parses("\"x\"", v));
    const auto mark = c.mark();
    c.key("joueurs");
    c.index(2);
    c.key(string("niveau"));
    nullable<std::int32_t> out;
    CHECK(!json::read_integer(v, out, c, "int"));
    CHECK(c.message() == string("$.joueurs[2].niveau : attendu int, reçu une chaîne"));
    CHECK(c.failed_path() == string("$.joueurs[2].niveau"));
    c.reset(mark);
    CHECK(c.path() == string("$"));
}

void test_writers() {
    mstring out;
    json::context c;
    json::write_string(out, string("a\"b\\c\n\x01 é"));
    CHECK(out.to_immutable() == string("\"a\\\"b\\\\c\\n\\u0001 é\""));

    mstring numbers;
    json::write_integer(numbers, std::int32_t{-5});
    numbers.push_back(' ');
    CHECK(json::write_real(numbers, 2.0, c));
    numbers.push_back(' ');
    CHECK(json::write_real(numbers, 0.1, c));
    numbers.push_back(' ');
    CHECK(json::write_real(numbers, 1e300, c));
    CHECK(numbers.to_immutable() == string("-5 2.0 0.1 1e+300"));

    CHECK(!json::write_real(numbers, std::numeric_limits<double>::infinity(), c));
    CHECK(!json::write_real(numbers, std::numeric_limits<double>::quiet_NaN(), c));

    mstring lines;
    json::newline(lines, 2);
    CHECK(lines.to_immutable() == string("\n    "));
}

}  // namespace

int main() {
    test_values();
    test_numbers();
    test_strings();
    test_errors();
    test_depth();
    test_hostile_documents();
    test_path();
    test_writers();
    return failures == 0 ? 0 : 1;
}
