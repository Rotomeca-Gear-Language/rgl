// Tests du runtime : rgl::standard::interpolate, l'écriture des valeurs dans les templates.
// Le programme renvoie 0 si tout passe, 1 sinon, et affiche chaque vérification ratée.

#include <cstdint>
#include <cstdio>
#include <limits>

#include "rgl/standard/format.hpp"

using rgl::standard::interpolate;
using rgl::standard::mstring;
using rgl::standard::string;

namespace {

int failures = 0;

#define CHECK_TEXT(expression, expected)                                                       \
    do {                                                                                        \
        const string actual = (expression);                                                     \
        if (!(actual == (expected))) {                                                          \
            std::fprintf(stderr, "ÉCHEC %s:%d : %s donne « %s », attendu « %s »\n", __FILE__,   \
                         __LINE__, #expression, actual.c_str(), (expected));                    \
            ++failures;                                                                         \
        }                                                                                       \
    } while (false)

void test_integers() {
    CHECK_TEXT(interpolate(std::int32_t{42}), "42");
    CHECK_TEXT(interpolate(std::int32_t{-7}), "-7");
    CHECK_TEXT(interpolate(std::uint32_t{4000000000u}), "4000000000");
    CHECK_TEXT(interpolate(std::numeric_limits<std::int64_t>::min()), "-9223372036854775808");
    CHECK_TEXT(interpolate(std::numeric_limits<std::uint64_t>::max()), "18446744073709551615");
    // Les entiers de 8 bits s'écrivent comme des nombres, pas comme des caractères.
    CHECK_TEXT(interpolate(std::int8_t{-5}, std::uint8_t{200}), "-5200");
}

void test_floats() {
    CHECK_TEXT(interpolate(0.1), "0.1");
    CHECK_TEXT(interpolate(2.0), "2.0");
    CHECK_TEXT(interpolate(-0.0), "-0.0");
    CHECK_TEXT(interpolate(1.0 / 3.0), "0.3333333333333333");
    CHECK_TEXT(interpolate(1e20), "1e+20");
    CHECK_TEXT(interpolate(0.1f), "0.1");
    CHECK_TEXT(interpolate(std::numeric_limits<double>::infinity()), "inf");
}

inline constexpr rgl::standard::symbol_entry literal_pret{"pret", 4};

void test_symbols() {
    CHECK_TEXT(interpolate(string("état = "), rgl::standard::symbol(literal_pret)), "état = pret");
}

void test_nullables() {
    CHECK_TEXT(interpolate(rgl::standard::nullable<std::int32_t>(4)), "4");
    CHECK_TEXT(interpolate(rgl::standard::nullable<std::int32_t>()), "null");
    CHECK_TEXT(interpolate(rgl::standard::nullable<string>(string("a"))), "a");
}

void test_with_size() {
    // Petite (dans l'objet) et longue (sur le tas) : une seule écriture, à la bonne taille.
    CHECK_TEXT(string::with_size(3, [](char* at) { at[0] = 'a'; at[1] = 'b'; at[2] = 'c'; }), "abc");
    const char* long_text = "trente caractères pour le tas.";
    CHECK_TEXT(string::with_size(31, [&](char* at) { for (int i = 0; i < 31; ++i) at[i] = long_text[i]; }), long_text);
    CHECK_TEXT(interpolate(string("a"), interpolate(std::int32_t{1}, string("b")), 2.5), "a1b2.5");
}

void test_unions() {
    using either = rgl::standard::union_of<std::int32_t, string>;
    CHECK_TEXT(interpolate(either(rgl::standard::member<0>, 7)), "7");
    CHECK_TEXT(interpolate(either(rgl::standard::member<1>, string("a"))), "\"a\"");
    using outcome = rgl::standard::result<std::int32_t, std::int32_t>;
    CHECK_TEXT(interpolate(outcome::ok(1), string(" "), outcome::err(2)), "ok(1) err(2)");
}

void test_mixed() {
    CHECK_TEXT(interpolate(true, string(" / "), false), "true / false");
    CHECK_TEXT(interpolate(string("total = "), std::int32_t{3}, string(", ratio = "), 1.5), "total = 3, ratio = 1.5");
    CHECK_TEXT(interpolate(mstring("m"), string("s")), "ms");
    CHECK_TEXT(interpolate(), "");
    // Plus long que la petite capacité : le texte passe sur le tas.
    CHECK_TEXT(interpolate(string("une phrase assez longue pour le tas : "), std::int32_t{123456789}),
               "une phrase assez longue pour le tas : 123456789");
}

}  // namespace

int main() {
    test_integers();
    test_floats();
    test_mixed();
    test_symbols();
    test_nullables();
    test_unions();
    test_with_size();

    if (failures != 0) std::fprintf(stderr, "%d vérification(s) ratée(s)\n", failures);
    return failures == 0 ? 0 : 1;
}
