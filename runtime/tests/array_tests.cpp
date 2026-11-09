// Tests du runtime : les collections — tableaux (array, fixed_array, bounded_array, non_empty_array),
// intervalles, dictionnaire et tuples —, la lecture par index ou par clé, l'ajout, le spread et l'écriture en texte. Un allocateur compteur vérifie qu'aucun bloc
// ne fuit. Le programme renvoie 0 si tout passe, 1 sinon, et affiche chaque vérification ratée.

#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>

#include "rgl/standard/array.hpp"
#include "rgl/standard/dictionary.hpp"
#include "rgl/standard/format.hpp"
#include "rgl/standard/range.hpp"
#include "rgl/standard/string.hpp"
#include "rgl/standard/symbol.hpp"
#include "rgl/standard/tuple.hpp"

using rgl::standard::AbiAllocator;
using rgl::standard::array;
using rgl::standard::bounded_array;
using rgl::standard::dictionary;
using rgl::standard::fixed_array;
using rgl::standard::interpolate;
using rgl::standard::make_array;
using rgl::standard::non_empty_array;
using rgl::standard::spread;
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

#define CHECK_TEXT(value, expected)                                                             \
    do {                                                                                        \
        const string text_ = (value);                                                           \
        if (std::strcmp(text_.data(), (expected)) != 0) {                                       \
            std::fprintf(stderr, "ÉCHEC %s:%d : « %s » au lieu de « %s »\n", __FILE__, __LINE__, \
                         text_.data(), (expected));                                             \
            ++failures;                                                                         \
        }                                                                                       \
    } while (false)

// Compte les blocs vivants : 0 à la fin de chaque test, sinon un bloc a fui.
struct Counting {
    static inline long live = 0;
    static void* allocate(const AbiAllocator&, std::size_t size) { ++live; return std::malloc(size); }
    static void* reallocate(const AbiAllocator&, void* block, std::size_t size) { return std::realloc(block, size); }
    static void release(const AbiAllocator&, void* block) { --live; std::free(block); }
    static constexpr AbiAllocator instance{&allocate, &reallocate, &release};
};

void test_dynamic() {
    {
        array<std::int32_t> numbers(Counting::instance);
        CHECK(numbers.empty() && numbers.size() == 0);
        for (std::int32_t i = 0; i < 100; ++i) numbers.push(i);
        CHECK(numbers.size() == 100 && numbers[42] == 42);
        CHECK(Counting::live == 1);   // un seul bloc, agrandi sur place

        // Une copie est une autre valeur : l'ajout dans l'une ne touche pas l'autre.
        array<std::int32_t> copy = numbers;
        copy.push(100);
        CHECK(copy.size() == 101 && numbers.size() == 100);
        CHECK(Counting::live == 2);

        // Le déplacement ne copie rien.
        array<std::int32_t> moved = static_cast<array<std::int32_t>&&>(copy);
        CHECK(moved.size() == 101 && copy.size() == 0 && Counting::live == 2);
    }
    CHECK(Counting::live == 0);

    // Des éléments non triviaux (chaînes sur le tas), déplacés quand le tableau grandit.
    {
        array<string> words;
        for (int i = 0; i < 20; ++i) words.push(interpolate(string("un mot assez long pour le tas : "), i));
        CHECK(words.size() == 20 && words[19] == string("un mot assez long pour le tas : 19"));

        // Ajouter un élément du tableau lui-même, au moment où il doit grandir.
        while (words.size() < words.capacity()) words.push(string("x"));
        words.push(words[0]);
        CHECK(words[words.size() - 1] == words[0]);
    }

    CHECK((array<std::int32_t>{1, 2, 3} == array<std::int32_t>{1, 2, 3}));
    CHECK(!(array<std::int32_t>{1, 2, 3} == array<std::int32_t>{1, 2}));
}

void test_index() {
    const array<std::int32_t> numbers{10, 20, 30};
    CHECK(numbers.has_index(0) && numbers.has_index(2u) && !numbers.has_index(3));
    CHECK(numbers.has_index(-1) && numbers.has_index(std::int64_t{-3}) && !numbers.has_index(-4) && !numbers.has_index(std::int64_t{-5}));
    CHECK(numbers.at(-1, "main.rgl:1:1") == 30 && numbers.at(-3, "main.rgl:1:1") == 10);
    CHECK(!numbers.has_index(UINT64_MAX));
    CHECK(numbers.at(1, "main.rgl:1:1") == 20);
    CHECK(numbers.try_at(2) != nullptr && *numbers.try_at(2) == 30);
    CHECK(numbers.try_at(3) == nullptr && numbers.try_at(-4) == nullptr && *numbers.try_at(-1) == 30);

    std::int32_t total = 0;
    for (const auto value : numbers) total += value;
    CHECK(total == 60);
}

void test_mutation() {
    // Les accès non const modifient l'élément sur place ; la copie garde les siens.
    array<std::int32_t> numbers{10, 20, 30};
    const array<std::int32_t> before = numbers;
    numbers.at(1, "main.rgl:1:1") = 21;
    numbers[2] += 1;
    CHECK(numbers[1] == 21 && numbers[2] == 31 && before[1] == 20);

    fixed_array<std::int32_t, 2> pair{1, 2};
    pair.at(0u, "main.rgl:2:1") = 5;
    CHECK(pair[0] == 5);

    bounded_array<std::int32_t, 0, 3> some{};
    some.push(1, "main.rgl:3:1");
    some[0] = 4;
    CHECK(some[0] == 4);

    non_empty_array<array<std::int32_t>> nested{array<std::int32_t>{1}};
    nested.at(0, "main.rgl:4:1").push(2);
    CHECK(nested[0].size() == 2 && nested[0][1] == 2);

    dictionary<string, bool> flags{{string("a"), false}};
    flags.at(string("a"), "main.rgl:5:1") = true;
    CHECK(flags[string("a")]);
    dictionary<std::int32_t, std::int32_t> counts{{1, 10}};
    counts[1] += 5;
    CHECK(counts.at(1, "main.rgl:6:1") == 15);
}

void test_spread() {
    const array<std::int32_t> a{1, 2};
    const fixed_array<std::int32_t, 2> b{8, 9};
    const auto all = make_array<std::int32_t>(spread(a), 3, spread(b), 4);
    CHECK((all == array<std::int32_t>{1, 2, 3, 8, 9, 4}));
    CHECK(all.capacity() == 6);   // la taille calculée d'abord : une seule allocation

    array<std::int32_t> self{1, 2, 3};
    self.append(self);            // un tableau étalé dans lui-même
    CHECK((self == array<std::int32_t>{1, 2, 3, 1, 2, 3}));

    CHECK(make_array<std::int32_t>().empty());
}

void test_fixed() {
    constexpr fixed_array<std::int32_t, 3> numbers{1, 2, 3};
    static_assert(numbers.size() == 3 && numbers[2] == 3);
    static_assert(numbers.has_index(2) && !numbers.has_index(3));
    static_assert(numbers == fixed_array<std::int32_t, 3>{1, 2, 3});
    static_assert(numbers.at(0, "main.rgl:1:1") == 1);

    const fixed_array<string, 2> words{string("a"), string("b")};
    CHECK(words[1] == string("b"));
}

void test_ranges() {
    using rgl::standard::range;

    // [0..10] : fin comprise, onze valeurs.
    constexpr auto inclusive = range<std::int32_t>(0, 10, 1, false, "main.rgl:1:1");
    static_assert(inclusive.size() == 11 && inclusive[0] == 0 && inclusive[10] == 10);

    // [0..<10] : fin exclue ; [0..10 by 3] : 0, 3, 6, 9.
    static_assert(range<std::int32_t>(0, 10, 1, true, "").size() == 10);
    constexpr auto stepped = range<std::int32_t>(0, 10, 3, false, "");
    static_assert(stepped.size() == 4 && stepped[3] == 9);

    // [10..0] : décroissant ; [10..<0 by -2] : 10, 8, 6, 4, 2.
    constexpr auto down = range<std::int32_t>(10, 0, -1, false, "");
    static_assert(down.size() == 11 && down[0] == 10 && down[10] == 0);
    constexpr auto down_even = range<std::int32_t>(10, 0, -2, true, "");
    static_assert(down_even.size() == 5 && down_even[4] == 2);

    // Un pas dans l'autre sens que les bornes, ou des bornes égales et exclues : vide.
    static_assert(range<std::int32_t>(0, 10, -1, false, "").size() == 0);
    static_assert(range<std::int32_t>(5, 5, 1, true, "").size() == 0);
    static_assert(range<std::int32_t>(5, 5, 1, false, "").size() == 1);

    // Aux extrémités des types : pas de débordement dans le calcul.
    constexpr auto wide = range<std::int64_t>(INT64_MIN, INT64_MAX, INT64_MAX, false, "");
    static_assert(wide.size() == 3 && wide[2] == INT64_MAX - 1);
    constexpr auto unsigned_down = range<std::uint64_t>(UINT64_MAX, UINT64_MAX - 2, -1, false, "");
    static_assert(unsigned_down.size() == 3 && unsigned_down[2] == UINT64_MAX - 2);

    // Un tableau se construit depuis la vue.
    const auto values = array<std::int32_t>::from(stepped);
    CHECK(values.size() == 4 && values[1] == 3);
    const auto fixed = fixed_array<std::int32_t, 4>::from(stepped, "main.rgl:2:1");
    CHECK(fixed[3] == 9);
}

void test_bounded() {
    using small = bounded_array<string, 1, 3>;
    small names{string("a")};
    CHECK(names.size() == 1 && names.can_push());
    names.push(string("b"), "main.rgl:2:1");
    names.push(string("c"), "main.rgl:3:1");
    CHECK(names.size() == 3 && !names.can_push());

    const small copy = names;
    CHECK(copy == names && copy[2] == string("c"));

    CHECK(small::fits(1) && small::fits(3) && !small::fits(0) && !small::fits(4));
    CHECK(small::try_from(array<string>{string("x"), string("y")}).has_value());
    CHECK(!small::try_from(array<string>{}).has_value());
    CHECK(!small::try_from(array<string>{string("1"), string("2"), string("3"), string("4")}).has_value());
    CHECK(small::from(array<string>{string("x")}, "main.rgl:4:1").size() == 1);

    bounded_array<std::int32_t, 0, 2> empty;
    CHECK(empty.empty());
    static_assert(bounded_array<std::int32_t, 0, 2>::capacity() == 2);
}

void test_non_empty() {
    non_empty_array<std::int32_t> numbers{5, 6};
    CHECK(numbers.first() == 5 && numbers.last() == 6);
    numbers.push(7);
    CHECK(numbers.last() == 7 && numbers.items().size() == 3);

    CHECK(!non_empty_array<std::int32_t>::try_from(array<std::int32_t>{}).has_value());
    CHECK(non_empty_array<std::int32_t>::try_from(array<std::int32_t>{1}).has_value());
    CHECK(non_empty_array<std::int32_t>::from(array<std::int32_t>{4}, "main.rgl:5:1").first() == 4);
}

void test_conversions() {
    const array<std::int32_t> three{1, 2, 3};
    CHECK((fixed_array<std::int32_t, 3>::from(three, "main.rgl:6:1") == fixed_array<std::int32_t, 3>{1, 2, 3}));
    CHECK((!fixed_array<std::int32_t, 2>::try_from(three).has_value()));
    CHECK((array<std::int32_t>::from(fixed_array<std::int32_t, 2>{4, 5}) == array<std::int32_t>{4, 5}));
    CHECK((bounded_array<std::int32_t, 2, 4>::from(three, "main.rgl:7:1").size() == 3));
    CHECK(non_empty_array<std::int32_t>::from(fixed_array<std::int32_t, 1>{9}, "main.rgl:8:1").first() == 9);
    CHECK(!non_empty_array<std::int32_t>::try_from(array<std::int32_t>{}).has_value());
}

void test_dictionary() {
    dictionary<string, std::int32_t> ages{{string("Zoé"), 7}, {string("Alix"), 12}};
    CHECK(ages.size() == 2 && ages.contains(string("Alix")) && !ages.contains(string("Bob")));
    CHECK(ages.at(string("Zoé"), "main.rgl:1:1") == 7);
    CHECK(ages.try_at(string("Bob")) == nullptr && *ages.try_at(string("Alix")) == 12);

    CHECK(ages.can_add(string("Bob")) && !ages.can_add(string("Zoé")));
    ages.add(string("Bob"), 30, "main.rgl:2:1");
    CHECK(ages.size() == 3 && ages[string("Bob")] == 30);

    // Rangé par clé, quel que soit l'ordre d'ajout.
    CHECK_TEXT(interpolate(ages), "[\"Alix\": 12, \"Bob\": 30, \"Zoé\": 7]");
    CHECK_TEXT(interpolate(dictionary<std::int32_t, bool>{}), "[:]");
    const dictionary<bool, bool> flags{{true, false}, {false, true}};
    CHECK(flags.at(true, "main.rgl:3:1") == false && flags[false]);
    CHECK_TEXT(interpolate(flags), "[false: true, true: false]");

    const dictionary<std::int32_t, std::int32_t> a{{1, 10}, {2, 20}};
    CHECK((a == dictionary<std::int32_t, std::int32_t>{{2, 20}, {1, 10}}));
    CHECK(!(a == dictionary<std::int32_t, std::int32_t>{{1, 10}}));

    // Des clés symboles, rangées par leur nom.
    static constexpr rgl::standard::symbol_entry rouge{"rouge", 5};
    static constexpr rgl::standard::symbol_entry bleu{"bleu", 4};
    const dictionary<rgl::standard::symbol, std::int32_t> colors{{rgl::standard::symbol(rouge), 1}, {rgl::standard::symbol(bleu), 2}};
    CHECK_TEXT(interpolate(colors), "[bleu: 2, rouge: 1]");
}

// Construit par de nombreux ajouts dans un ordre brouillé : le tampon est versé plusieurs fois, et toutes les lectures voient toutes les clés.
void test_dictionary_batches() {
    dictionary<std::int32_t, std::int32_t> table;
    constexpr std::int32_t count = 5000;
    for (std::int32_t i = 0; i < count; ++i) {
        const std::int32_t key = (i * 7919) % count;   // un ordre brouillé, sans doublon (7919 est premier avec 5000)
        table.add(key, key * 2, "test");
        CHECK(table.contains(key));
        CHECK(table.size() == static_cast<std::size_t>(i + 1));
    }
    for (std::int32_t key = 0; key < count; ++key) CHECK(table.at(key, "test") == key * 2);
    CHECK(!table.contains(count));
    CHECK(table.try_at(count) == nullptr);

    // Le parcours voit tout, dans l'ordre des clés.
    std::int32_t expected = 0;
    for (const auto& [key, value] : table) {
        CHECK(key == expected && value == expected * 2);
        ++expected;
    }
    CHECK(expected == count);

    // Une valeur modifiée avant et après un versement.
    dictionary<std::int32_t, std::int32_t> small;
    for (std::int32_t i = 0; i < 40; ++i) small.add(i, i, "test");
    small.at(3, "test") = 99;
    small.add(1000, 1, "test");
    small.at(1000, "test") = 5;
    CHECK(small.at(3, "test") == 99 && small.at(1000, "test") == 5);

    // Copie, comparaison, put et without voient les ajouts encore dans le tampon.
    dictionary<std::int32_t, std::int32_t> a;
    dictionary<std::int32_t, std::int32_t> b;
    for (std::int32_t i = 0; i < 10; ++i) a.add(i, i, "test");
    for (std::int32_t i = 9; i >= 0; --i) b.add(i, i, "test");
    CHECK(a == b);
    const auto copy = a;
    CHECK(copy == b && copy.size() == 10);
    CHECK(a.put(3, 30).at(3, "test") == 30 && a.put(20, 1).size() == 11 && a.without(3).size() == 9 && !a.without(3).contains(3));
    CHECK(a.size() == 10);
}

void test_tuple() {
    constexpr rgl::standard::tuple<std::int32_t, bool> pair{3, true};
    static_assert(rgl::standard::item<0>(pair) == 3 && rgl::standard::item<1>(pair));
    CHECK_TEXT(interpolate(rgl::standard::tuple<std::int32_t, string>{1, string("a")}), "(1, \"a\")");
    CHECK((rgl::standard::tuple<std::int32_t, std::int32_t>{1, 2} == rgl::standard::tuple<std::int32_t, std::int32_t>{1, 2}));
}

void test_text() {
    CHECK_TEXT(interpolate(array<std::int32_t>{1, 2, 3}), "[1, 2, 3]");
    CHECK_TEXT(interpolate(array<std::int32_t>{}), "[]");
    CHECK_TEXT(interpolate(array<string>{string("a"), string("b")}), "[\"a\", \"b\"]");
    CHECK_TEXT(interpolate(fixed_array<double, 2>{1.5, 2.0}), "[1.5, 2.0]");
    CHECK_TEXT(interpolate(array<array<std::int32_t>>{array<std::int32_t>{1}, array<std::int32_t>{}}), "[[1], []]");
    CHECK_TEXT(interpolate(non_empty_array<bool>{true}), "[true]");
}

}  // namespace

int main() {
    test_dynamic();
    test_index();
    test_mutation();
    test_spread();

    test_fixed();
    test_ranges();
    test_bounded();
    test_non_empty();
    test_conversions();
    test_dictionary();
    test_dictionary_batches();
    test_tuple();
    test_text();
    CHECK(Counting::live == 0);

    if (failures != 0) std::fprintf(stderr, "%d vérification(s) ratée(s)\n", failures);
    return failures == 0 ? 0 : 1;
}
