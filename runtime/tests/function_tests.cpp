// Tests du runtime : rgl::standard::function, le type des lambdas RGL. Une petite lambda est rangée
// dans la function, une grande sur le tas, partagée par les copies ; un allocateur compteur vérifie
// qu'aucun bloc ne fuit. Le programme renvoie 0 si tout passe, 1 sinon, et affiche chaque
// vérification ratée.

#include <cstdint>
#include <cstdio>
#include <cstdlib>

#include "rgl/standard/array.hpp"
#include "rgl/standard/function.hpp"
#include "rgl/standard/string.hpp"

using rgl::standard::AbiAllocator;
using rgl::standard::array;
using rgl::standard::function;
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

// Compte les blocs vivants : 0 à la fin de chaque test, sinon un bloc a fui.
struct Counting {
    static inline long live = 0;
    static void* allocate(const AbiAllocator&, std::size_t size) { ++live; return std::malloc(size); }
    static void* reallocate(const AbiAllocator&, void* block, std::size_t size) { return std::realloc(block, size); }
    static void release(const AbiAllocator&, void* block) { --live; std::free(block); }
    static constexpr AbiAllocator instance{&allocate, &reallocate, &release};
};

std::int32_t twice(std::int32_t n) { return n * 2; }

void test_inline() {
    const std::int32_t threshold = 10;
    const function<bool(std::int32_t)> above([=](std::int32_t v) { return v > threshold; }, Counting::instance);
    CHECK(!above.on_heap() && Counting::live == 0);
    CHECK(above(11) && !above(10));

    // Une copie, une affectation, un déplacement : la même lambda.
    function<bool(std::int32_t)> copy = above;
    CHECK(copy(12));
    function<bool(std::int32_t)> other([](std::int32_t v) { return v < 0; });
    other = copy;
    CHECK(other(20) && !other(-1));
    function<bool(std::int32_t)> moved = static_cast<function<bool(std::int32_t)>&&>(copy);
    CHECK(moved(15));

    // Un gear passé par son nom : un pointeur de fonction.
    const function<std::int32_t(std::int32_t)> doubled(&twice);
    CHECK(!doubled.on_heap() && doubled(21) == 42);
}

void test_heap() {
    {
        // Des valeurs non constantes : une constante entière ne serait pas capturée.
        std::int64_t a = 1, b = 2, c = 3, d = 4;
        a += 0;
        const function<std::int64_t()> sum([=]() { return a + b + c + d; }, Counting::instance);
        CHECK(sum.on_heap() && Counting::live == 1);
        CHECK(sum() == 10);

        // Les copies partagent le bloc : aucune allocation de plus.
        function<std::int64_t()> copy = sum;
        function<std::int64_t()> moved = static_cast<function<std::int64_t()>&&>(copy);
        CHECK(Counting::live == 1 && moved() == 10 && copy() == 10);

        // Une affectation libère l'ancienne lambda quand elle n'est plus partagée.
        function<std::int64_t()> replaced([=]() { return a + b + c + d + 1; }, Counting::instance);
        CHECK(Counting::live == 2);
        replaced = sum;
        CHECK(Counting::live == 1 && replaced() == 10);
    }
    CHECK(Counting::live == 0);
}

void test_captures() {
    {
        // Une chaîne et un tableau capturés par copie : la lambda garde leur valeur de création.
        array<std::int32_t> values(Counting::instance);
        values.push(1);
        values.push(2);
        const string name("une phrase de plus de vingt-deux octets");
        const function<std::uint32_t(const string&)> count([=](const string& suffix) {
            return static_cast<std::uint32_t>(values.size() + name.size() + suffix.size());
        });
        values.push(3);
        CHECK(count(string("ab")) == 2 + 39 + 2);

        // Une lambda sans valeur de retour.
        std::int32_t calls = 0;
        const function<void(std::int32_t)> record([&calls](std::int32_t n) { calls += n; });
        record(3);
        record(4);
        CHECK(calls == 7);

        // Une closure peut conserver les modifications explicites de sa propre copie capturée.
        const function<std::int32_t()> next([value = std::int32_t{0}]() mutable { return ++value; });
        CHECK(next() == 1 && next() == 2);

        // Un tableau de lambdas.
        array<function<std::int32_t(std::int32_t)>> operations;
        operations.push(function<std::int32_t(std::int32_t)>([](std::int32_t n) { return n + 1; }));
        operations.push(function<std::int32_t(std::int32_t)>(&twice));
        CHECK(operations[0](5) == 6 && operations[1](5) == 10);
    }
    CHECK(Counting::live == 0);
}

}  // namespace

int main() {
    test_inline();
    test_heap();
    test_captures();
    if (failures == 0) std::printf("function : tout passe\n");
    return failures == 0 ? 0 : 1;
}
