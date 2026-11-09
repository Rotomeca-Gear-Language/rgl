// Tests du runtime : rgl::standard::regex, l'expression régulière de RGL (moteur CTRE).
// Le programme renvoie 0 si tout passe, 1 sinon, et affiche chaque vérification ratée.

#include <cstdio>
#include <cstring>

#include "rgl/standard/regex.hpp"

using rgl::standard::array;
using rgl::standard::make_regex;
using rgl::standard::nullable;
using rgl::standard::regex;
using rgl::standard::string;

namespace {

int failures = 0;

#define CHECK(condition)                                                                        \
    do {                                                                                        \
        if (!(condition)) {                                                                     \
            std::fprintf(stderr, "ÉCHEC %s:%d : %s\n", __FILE__, __LINE__, #condition);         \
            ++failures;                                                                         \
        }                                                                                       \
    } while (false)

#define CHECK_TEXT(expression, expected)                                                         \
    do {                                                                                        \
        const string actual = (expression);                                                     \
        if (std::strcmp(actual.c_str(), (expected)) != 0) {                                     \
            std::fprintf(stderr, "ÉCHEC %s:%d : %s donne « %s », attendu « %s »\n", __FILE__,   \
                         __LINE__, #expression, actual.c_str(), (expected));                    \
            ++failures;                                                                         \
        }                                                                                       \
    } while (false)

struct word {
    static constexpr auto first = ctre::search<u8R"rgl(\w+)rgl">;
    static constexpr auto all = ctre::search_all<u8R"rgl(\w+)rgl">;
};

struct pair_re {
    static constexpr auto first = ctre::search<u8R"rgl((\w+)=(\d+)?)rgl">;
    static constexpr auto all = ctre::search_all<u8R"rgl((\w+)=(\d+)?)rgl">;
};

struct caseless {
    static constexpr auto first = ctre::search<u8"abc", ctre::case_insensitive>;
    static constexpr auto all = ctre::search_all<u8"abc", ctre::case_insensitive>;
};

struct lines {
    static constexpr auto first = ctre::multiline_search<u8"^b$">;
    static constexpr auto all = ctre::multiline_search_all<u8"^b$">;
};

struct accents {
    static constexpr auto first = ctre::search<u8"é.">;
    static constexpr auto all = ctre::search_all<u8"é.">;
};

constexpr regex first_word = make_regex<word, false>();
constexpr regex every_word = make_regex<word, true>();
constexpr regex caseless_regex = make_regex<caseless, false>();
constexpr regex lines_regex = make_regex<lines, false>();
constexpr regex pairs_regex = make_regex<pair_re, true>();

void test_test() {
    CHECK(first_word.test(string("abc def")));
    CHECK(!first_word.test(string("  ")));
    CHECK(caseless_regex.test(string("xxABcxx")));
    CHECK(lines_regex.test(string("a\nb\nc")));
    CHECK(!lines_regex.test(string("ab")));
}

void test_match() {
    constexpr regex r = make_regex<pair_re, false>();
    const nullable<array<string>> none = r.match(string("???"));
    CHECK(!none.has_value());

    const nullable<array<string>> found = r.match(string("x=12 y="));
    CHECK(found.has_value());
    CHECK((*found).size() == 3);
    CHECK_TEXT((*found)[0], "x=12");
    CHECK_TEXT((*found)[1], "x");
    CHECK_TEXT((*found)[2], "12");

    // Un groupe qui n'a pas participé est vide.
    const nullable<array<string>> partial = r.match(string("y="));
    CHECK(partial.has_value());
    CHECK_TEXT((*partial)[1], "y");
    CHECK_TEXT((*partial)[2], "");
}

void test_find() {
    const array<string> one = first_word.find(string("un deux trois"));
    CHECK(one.size() == 1);
    CHECK_TEXT(one[0], "un");

    const array<string> every = every_word.find(string("un deux trois"));
    CHECK(every.size() == 3);
    CHECK_TEXT(every[2], "trois");

    CHECK(every_word.find(string("  ")).size() == 0);
}

void test_replace() {
    CHECK_TEXT(first_word.replace(string("un deux"), string("X")), "X deux");
    CHECK_TEXT(every_word.replace(string("un deux"), string("X")), "X X");
    CHECK_TEXT(every_word.replace(string("un deux"), string("<$0>")), "<un> <deux>");
    CHECK_TEXT(every_word.replace(string("un deux"), string("$$ $5 $")), "$  $ $  $");
    CHECK_TEXT(every_word.replace(string(""), string("X")), "");
    CHECK_TEXT(pairs_regex.replace(string("a=1, b=2"), string("$2:$1")), "1:a, 2:b");
}

void test_unicode() {
    constexpr regex r = make_regex<accents, true>();
    // Le point voit un caractère Unicode entier, pas un octet.
    const array<string> found = r.find(string("été éï"));
    CHECK(found.size() == 3);
    CHECK_TEXT(found[0], "ét");
    CHECK_TEXT(found[1], "é ");
    CHECK_TEXT(found[2], "éï");
}

void test_value() {
    // Un regex est une valeur : copiée, comparée, réaffectée.
    regex a = first_word;
    regex b = every_word;
    CHECK(a == first_word);
    CHECK(!(a == b));
    a = b;
    CHECK(a == b);
    CHECK(a.find(string("a b")).size() == 2);
}

}  // namespace

int main() {
    test_test();
    test_match();
    test_find();
    test_replace();
    test_unicode();
    test_value();
    return failures == 0 ? 0 : 1;
}
