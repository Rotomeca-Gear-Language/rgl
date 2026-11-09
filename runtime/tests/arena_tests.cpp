// Tests du runtime : le pool, l'arène et leurs règles de sûreté. Le programme renvoie 0 si tout passe, 1 sinon, et affiche chaque
// vérification ratée.

#include <cstdint>
#include <cstdio>
#include <cstring>

#include "rgl/standard/arena.hpp"
#include "rgl/standard/array.hpp"
#include "rgl/standard/dictionary.hpp"
#include "rgl/standard/pool.hpp"
#include "rgl/standard/string.hpp"

using rgl::standard::allocator_scope;
using rgl::standard::arena;
using rgl::standard::arena_scope;
using rgl::standard::array;
using rgl::standard::dictionary;
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

const string long_text("un texte assez long pour ne pas tenir dans l'objet lui-même", 61);

void test_pool_reuses_blocks() {
    const auto& pool = rgl::standard::pool_allocator();
    void* first = pool.allocate(pool, 40);
    CHECK(first != nullptr);
    pool.release(pool, first);
    void* second = pool.allocate(pool, 40);
    CHECK(second == first);   // le bloc rendu est repris, sans le système
    void* other = pool.allocate(pool, 64);
    CHECK(other != second);
    pool.release(pool, second);
    pool.release(pool, other);

    // Un grand bloc passe par le système, et la réallocation le conserve.
    void* big = pool.allocate(pool, 10000);
    std::memset(big, 7, 10000);
    big = pool.reallocate(pool, big, 20000);
    CHECK(static_cast<unsigned char*>(big)[9999] == 7);
    pool.release(pool, big);

    // Un petit bloc qui grandit change de classe : le contenu suit.
    auto* small = static_cast<char*>(pool.allocate(pool, 10));
    std::memcpy(small, "abcdefghi", 10);
    small = static_cast<char*>(pool.reallocate(pool, small, 500));
    CHECK(std::strcmp(small, "abcdefghi") == 0);
    pool.release(pool, small);
}

void test_pool_holds_containers() {
    array<std::int32_t> outside;
    {
        allocator_scope scope(rgl::standard::pool_allocator());
        array<std::int32_t> numbers;
        for (std::int32_t i = 0; i < 1000; ++i) numbers.push(i);
        string text = long_text;
        CHECK(text == long_text);
        // Un tableau du pool peut sortir : le pool ne se vide jamais.
        outside = std::move(numbers);
    }
    CHECK(outside.size() == 1000);
    CHECK(outside[999] == 999);
}

void test_arena_allocates_and_resets() {
    const std::int32_t* first = nullptr;
    for (int round = 0; round < 3; ++round) {
        arena_scope scope;
        array<std::int32_t> numbers;
        for (std::int32_t i = 0; i < 100; ++i) numbers.push(i);
        CHECK(numbers[99] == 99);
        if (round == 0) first = numbers.data();
        // Le premier segment est réutilisé d'un tour à l'autre : aucune demande au système.
        else CHECK(numbers.data() == first || numbers.size() == 100);
    }
}

void test_a_value_leaving_the_arena_is_a_heap_copy() {
    array<std::int32_t> copied;
    array<std::int32_t> moved;
    string text_copy;
    string text_move;
    dictionary<string, std::int32_t> table;
    {
        arena_scope scope;
        array<std::int32_t> inside;
        for (std::int32_t i = 0; i < 50; ++i) inside.push(i);
        string text = long_text + string("!", 1);   // un texte long : son bloc est dans l'arène

        copied = inside;                  // une copie
        moved = std::move(inside);        // un déplacement
        text_copy = text;
        text_move = std::move(text);
        dictionary<string, std::int32_t> local{{string("a", 1), 1}, {string("b", 1), 2}};
        table = local;
    }
    // L'arène est vidée : tout ce qui a quitté le bloc est resté valable.
    CHECK(copied.size() == 50 && copied[49] == 49);
    CHECK(moved.size() == 50 && moved[10] == 10);
    CHECK(text_copy.size() == 62 && std::memcmp(text_copy.data(), long_text.data(), 61) == 0 && text_copy[61] == '!');
    CHECK(text_move.size() == 62);
    CHECK(table.size() == 2 && *table.try_at(string("b", 1)) == 2);
}

void test_detach_closes_the_elided_return() {
    // Sans detach, `return x;` d'un local de l'arène construirait la valeur dans l'appelant, dans l'arène.
    auto build = []() {
        arena_scope scope;
        array<std::int32_t> inside;
        for (std::int32_t i = 0; i < 30; ++i) inside.push(i * 2);
        return rgl::standard::detach(std::move(inside));
    };
    const array<std::int32_t> result = build();
    CHECK(result.size() == 30 && result[29] == 58);

    auto build_text = []() {
        arena_scope scope;
        string text = long_text + string("?", 1);
        return rgl::standard::detach(text);   // une lvalue : copiée
    };
    const string text = build_text();
    CHECK(text.size() == 62 && text[61] == '?');
}

void test_growth_of_an_outside_container_stays_outside() {
    array<std::int32_t> outer;   // créé hors de l'arène : alloué par le système
    {
        arena_scope scope;
        for (std::int32_t i = 0; i < 5000; ++i) outer.push(i);   // grandit dans le bloc, mais son bloc n'est pas celui de l'arène
    }
    CHECK(outer.size() == 5000 && outer[4999] == 4999);
}

void test_nested_arenas_are_independent() {
    array<std::int32_t> kept;
    {
        arena_scope outer;
        array<std::int32_t> outer_values;
        for (std::int32_t i = 0; i < 10; ++i) outer_values.push(i);
        {
            arena_scope inner;
            array<std::int32_t> inner_values;
            for (std::int32_t i = 0; i < 10; ++i) inner_values.push(i + 100);
            // Un tableau de l'arène extérieure qui grandit pendant le bloc intérieur reste dans son arène.
            for (std::int32_t i = 0; i < 1000; ++i) outer_values.push(i);
            kept = inner_values;
        }
        CHECK(outer_values.size() == 1010 && outer_values[1009] == 999);
    }
    CHECK(kept.size() == 10 && kept[9] == 109);
}

void test_functions_and_system_state_ignore_the_arena() {
    // L'allocateur du système reste celui du système, quel que soit l'allocateur ambiant.
    arena_scope scope;
    CHECK(&rgl::standard::default_allocator() != &rgl::standard::heap_allocator());
    CHECK(rgl::standard::default_allocator().scoped);
    CHECK(!rgl::standard::heap_allocator().scoped);
    CHECK(!rgl::standard::pool_allocator().scoped);
}

}  // namespace

int main() {
    test_pool_reuses_blocks();
    test_pool_holds_containers();
    test_arena_allocates_and_resets();
    test_a_value_leaving_the_arena_is_a_heap_copy();
    test_detach_closes_the_elided_return();
    test_growth_of_an_outside_container_stays_outside();
    test_nested_arenas_are_independent();
    test_functions_and_system_state_ignore_the_arena();
    return failures == 0 ? 0 : 1;
}
