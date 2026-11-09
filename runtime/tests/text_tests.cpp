// Tests du runtime : le compte de caractères mis en cache et les opérations de texte (text.hpp). Renvoie 0 si tout passe.

#include <cstdio>
#include <cstring>

#include "rgl/standard/text.hpp"

using rgl::standard::array;
using rgl::standard::string;
namespace text = rgl::standard::text;

namespace {

int failures = 0;

#define CHECK(condition)                                                                        \
    do {                                                                                        \
        if (!(condition)) {                                                                     \
            std::fprintf(stderr, "ÉCHEC %s:%d : %s\n", __FILE__, __LINE__, #condition);         \
            ++failures;                                                                         \
        }                                                                                       \
    } while (false)

bool same(const string& value, const char* expected) {
    return value.size() == std::strlen(expected) && std::memcmp(value.data(), expected, value.size()) == 0;
}

void test_length_is_cached_and_shared() {
    const string ascii("un texte assez long pour vivre sur le tas, sans accent", 54);
    CHECK(ascii.length() == 54 && ascii.is_ascii());
    const string copy = ascii;   // la copie partage le bloc, donc le compte déjà fait
    CHECK(copy.length() == 54 && copy.is_ascii());
    CHECK(same(copy.character(3), "t") && same(copy.character(53), "t") && copy.character(54).size() == 0);

    const string accents("é à ü un texte assez long pour vivre sur le tas — ça sert", std::strlen("é à ü un texte assez long pour vivre sur le tas — ça sert"));
    CHECK(!accents.is_ascii() && accents.length() < accents.size());
    CHECK(same(accents.character(0), "é") && same(accents.character(2), "à"));
}

void test_substring() {
    const string s("héllo wörld 😀 fin", std::strlen("héllo wörld 😀 fin"));
    CHECK(same(text::substring(s, 1, 5), "éllo"));
    CHECK(same(text::substring(s, -3, 100), "fin"));
    CHECK(same(text::substring(s, 12, 13), "😀"));
    CHECK(text::substring(s, 7, 3).size() == 0);
    CHECK(same(text::substring(s, -100, 2), "hé"));
    CHECK(text::substring(string(), 0, 5).size() == 0);
}

void test_search() {
    const string s("héllo wörld 😀 fin", std::strlen("héllo wörld 😀 fin"));
    CHECK(text::index_of(s, string("wör", 4)) == 6);
    CHECK(text::index_of(s, string("😀", 4)) == 12);
    CHECK(text::index_of(s, string("zzz", 3)) == -1);
    CHECK(text::index_of(s, string()) == 0);
    CHECK(text::last_index_of(s, string("l", 1)) == 9);
    CHECK(text::last_index_of(s, string()) == 17);
}

void test_split_join_replace() {
    const string s("a, é, b,,c", std::strlen("a, é, b,,c"));
    const array<string> parts = text::split(s, string(", ", 2));
    CHECK(parts.size() == 3 && same(parts[1], "é") && same(parts[2], "b,,c"));
    CHECK(same(text::join(parts, string("+", 1)), "a+é+b,,c"));
    CHECK(text::split(s, string(",", 1)).size() == 5);
    CHECK(text::split(s, string()).size() == 1);
    CHECK(text::split(string(), string(",", 1)).size() == 1);

    CHECK(same(text::replace(s, string(", ", 2), string(";", 1)), "a;é;b,,c"));
    CHECK(same(text::replace(s, string("é", 2), string("ee", 2)), "a, ee, b,,c"));
    CHECK(same(text::replace(s, string(), string("x", 1)), "a, é, b,,c"));
    CHECK(same(text::replace(s, string("zzz", 3), string("x", 1)), "a, é, b,,c"));
    CHECK(text::join(array<string>(), string(",", 1)).size() == 0);
}

void test_case_repeat_reverse() {
    CHECK(same(text::upper(string("héllo Wörld 1", std::strlen("héllo Wörld 1"))), "HéLLO WöRLD 1"));
    CHECK(same(text::lower(string("HÉLLO wörld 1", std::strlen("HÉLLO wörld 1"))), "hÉllo wörld 1"));
    CHECK(same(text::repeat(string("é", 2), 3), "ééé") && text::repeat(string("x", 1), 0).size() == 0);
    CHECK(same(text::reverse(string("héllo😀", std::strlen("héllo😀"))), "😀olléh"));
    CHECK(text::reverse(string()).size() == 0 && same(text::reverse(string("a", 1)), "a"));
}

}  // namespace

int main() {
    test_length_is_cached_and_shared();
    test_substring();
    test_search();
    test_split_join_replace();
    test_case_repeat_reverse();
    return failures == 0 ? 0 : 1;
}
