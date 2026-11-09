// Tests du runtime : rgl::standard::symbol et la table des symboles.
// Le programme renvoie 0 si tout passe, 1 sinon, et affiche chaque vérification ratée.

#include <cstdio>
#include <cstring>
#include <string>
#include <thread>
#include <vector>

#include "rgl/standard/symbol.hpp"

using rgl::standard::symbol;
using rgl::standard::symbol_entry;

namespace {

int failures = 0;

#define CHECK(condition)                                                                \
    do {                                                                                \
        if (!(condition)) {                                                             \
            std::fprintf(stderr, "ÉCHEC %s:%d : %s\n", __FILE__, __LINE__, #condition); \
            ++failures;                                                                 \
        }                                                                               \
    } while (false)

// Comme le code généré : des littéraux statiques, uniques grâce à inline.
inline constexpr symbol_entry literal_pret{"pret", 4};
inline constexpr symbol_entry literal_fini{"fini", 4};

// Un littéral est utilisable à la compilation : construction et comparaison sont constexpr.
static_assert(symbol(literal_pret) == symbol(literal_pret));
static_assert(!(symbol(literal_pret) == symbol(literal_fini)));
static_assert(symbol(literal_pret).size() == 4);

bool has_name(symbol s, const char* name) {
    return s.size() == std::strlen(name) && std::memcmp(s.data(), name, s.size()) == 0;
}

void test_literals() {
    constexpr symbol pret(literal_pret);
    CHECK(has_name(pret, "pret"));
    CHECK(pret == symbol(literal_pret));
    CHECK(!(pret == symbol(literal_fini)));
}

void test_intern_gives_one_entry_per_name() {
    const symbol a = symbol::intern("alpha", 5);
    const symbol b = symbol::intern("alpha", 5);
    const symbol c = symbol::intern("alphabet", 8);
    CHECK(a == b);
    CHECK(!(a == c));
    CHECK(has_name(a, "alpha"));
    CHECK(has_name(c, "alphabet"));

    // Le nom est copié : modifier le texte d'origine ne change pas le symbole.
    char buffer[] = "beta";
    const symbol beta = symbol::intern(buffer, 4);
    buffer[0] = 'z';
    CHECK(has_name(beta, "beta"));
    CHECK(beta == symbol::intern("beta", 4));

    // Le nom vide est un symbole comme un autre.
    CHECK(symbol::intern("", 0) == symbol::intern("", 0));
}

void test_registered_literals_are_found_by_intern() {
    symbol::register_literal(literal_pret);
    symbol::register_literal(literal_pret);  // deux fois : sans effet
    CHECK(symbol::intern("pret", 4) == symbol(literal_pret));

    // Un nom déjà interné garde son entrée : le littéral inscrit après ne la remplace pas.
    const symbol first = symbol::intern("fini", 4);
    symbol::register_literal(literal_fini);
    CHECK(symbol::intern("fini", 4) == first);
}

void test_table_grows() {
    std::vector<symbol> symbols;
    for (int i = 0; i < 5000; ++i) {
        const std::string name = "nom_" + std::to_string(i);
        symbols.push_back(symbol::intern(name.data(), name.size()));
    }
    for (int i = 0; i < 5000; ++i) {
        const std::string name = "nom_" + std::to_string(i);
        CHECK(symbols[static_cast<std::size_t>(i)] == symbol::intern(name.data(), name.size()));
    }
}

void test_threads_agree_on_identity() {
    constexpr int thread_count = 8;
    constexpr int names = 500;
    std::vector<std::vector<symbol>> seen(thread_count);
    std::vector<std::thread> threads;
    for (int t = 0; t < thread_count; ++t) {
        threads.emplace_back([t, &seen] {
            for (int i = 0; i < names; ++i) {
                // Chaque thread parcourt les noms dans un ordre différent.
                const int n = (i * (t + 1) * 7) % names;
                const std::string name = "parallele_" + std::to_string(n);
                seen[static_cast<std::size_t>(t)].push_back(symbol::intern(name.data(), name.size()));
            }
        });
    }
    for (auto& thread : threads) thread.join();

    for (int i = 0; i < names; ++i) {
        const std::string name = "parallele_" + std::to_string(i);
        const symbol expected = symbol::intern(name.data(), name.size());
        for (int t = 0; t < thread_count; ++t) {
            for (int j = 0; j < names; ++j) {
                if ((j * (t + 1) * 7) % names == i) CHECK(seen[static_cast<std::size_t>(t)][static_cast<std::size_t>(j)] == expected);
            }
        }
    }
}

}  // namespace

int main() {
    test_literals();
    test_intern_gives_one_entry_per_name();
    test_registered_literals_are_found_by_intern();
    test_table_grows();
    test_threads_agree_on_identity();

    if (failures != 0) std::fprintf(stderr, "%d vérification(s) ratée(s)\n", failures);
    return failures == 0 ? 0 : 1;
}
