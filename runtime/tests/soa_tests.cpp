// Tests du runtime : soa_array, le tableau de molds rangé en colonnes. Le Layout est écrit ici à la
// main, comme le compilateur le génère : un mold avec un sous-mold aplati et une chaîne. Le
// programme renvoie 0 si tout passe, 1 sinon, et affiche chaque vérification ratée.

#include <cstdint>
#include <cstdio>
#include <cstring>

#include "rgl/standard/array.hpp"
#include "rgl/standard/format.hpp"
#include "rgl/standard/soa.hpp"
#include "rgl/standard/string.hpp"

using rgl::standard::array;
using rgl::standard::fixed_array;
using rgl::standard::interpolate;
using rgl::standard::make_soa;
using rgl::standard::soa_array;
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

struct Vec2 {
    double x;
    double y;
    friend bool operator==(const Vec2&, const Vec2&) = default;
};

struct Entity {
    string name;
    Vec2 position;
    std::int32_t mass;
    friend bool operator==(const Entity&, const Entity&) = default;

    template <class Out>
    friend void rgl_write(Out& out, const Entity& value) {
        out.append("{name: ", 7);
        rgl::standard::detail::write_field(out, value.name);
        out.append(", mass: ", 8);
        rgl::standard::detail::write_field(out, value.mass);
        out.push_back('}');
    }
};

// Colonnes rangées comme le compilateur le fait : les plus chaudes d'abord, puis par alignement.
struct EntityLayout {
    using value_type = Entity;
    struct columns {
        array<double> k0;          // position.x
        array<double> k1;          // position.y
        array<std::int32_t> k2;    // mass
        array<string> k3;          // name
    };
    static value_type gather(const columns& c, std::size_t i) { return Entity{c.k3[i], Vec2{c.k0[i], c.k1[i]}, c.k2[i]}; }
    static void scatter(columns& c, const value_type& v) {
        c.k0.push(v.position.x);
        c.k1.push(v.position.y);
        c.k2.push(v.mass);
        c.k3.push(v.name);
    }
    static void store(columns& c, std::size_t i, const value_type& v) {
        c.k0[i] = v.position.x;
        c.k1[i] = v.position.y;
        c.k2[i] = v.mass;
        c.k3[i] = v.name;
    }
    static void reserve(columns& c, std::size_t n) {
        c.k0.reserve(n);
        c.k1.reserve(n);
        c.k2.reserve(n);
        c.k3.reserve(n);
    }
};

using Entities = soa_array<EntityLayout>;

void test_basics() {
    Entities empty;
    CHECK(empty.empty() && empty.size() == 0 && !empty.has_index(0));

    Entities all{{string("a"), {1.0, 2.0}, 3}, {string("b"), {4.0, 5.0}, 6}};
    CHECK(all.size() == 2 && all.has_index(1) && !all.has_index(2) && all.has_index(-1) && !all.has_index(-3));
    CHECK(all.at(-1, "main.rgl:1:1").mass == 6);
    CHECK(all[1].name == "b" && all[1].position.y == 5.0 && all[1].mass == 6);
    CHECK(all.at(0, "main.rgl:1:1").mass == 3);
    CHECK(all.try_at(1).has_value() && !all.try_at(5).has_value());

    all.push(Entity{string("une phrase de plus de vingt-deux octets"), {7.0, 8.0}, 9});
    CHECK(all.size() == 3 && all[2].name == "une phrase de plus de vingt-deux octets");

    // Le spread de lui-même, et l'égalité.
    Entities copy = all;
    copy.append(copy);
    CHECK(copy.size() == 6 && copy[5].mass == 9 && copy[3].name == "a");
    CHECK(all == all && !(all == copy));
}

void test_mutation() {
    Entities all{{string("a"), {1.0, 2.0}, 3}, {string("b"), {4.0, 5.0}, 6}};

    // Un élément entier remplacé, puis un champ modifié dans sa colonne.
    all.set(all.index(1, "main.rgl:1:1"), Entity{string("c"), {7.0, 8.0}, 9});
    CHECK(all[1].name == "c" && all[1].position.x == 7.0 && all[1].mass == 9 && all[0].mass == 3);
    all.columns().k2[all.index(0, "main.rgl:2:1")] = 42;
    CHECK(all[0].mass == 42 && all[0].name == "a");

    // Une copie ne partage rien avec l'original.
    Entities copy = all;
    copy.columns().k0[0] = -1.0;
    CHECK(all[0].position.x == 1.0 && copy[0].position.x == -1.0);
}

void test_conversions() {
    const Entities all{{string("a"), {1.0, 2.0}, 3}, {string("b"), {4.0, 5.0}, 6}};

    // Vers et depuis les autres tableaux : la source n'est lue que par size() et operator[].
    const auto fixed = fixed_array<Entity, 2>::from(all, "main.rgl:2:1");
    CHECK(fixed[1].mass == 6);
    const auto back = Entities::from(fixed);
    CHECK(back == all);
    const auto plain = array<Entity>::from(all);
    CHECK(plain.size() == 2 && plain[0].name == "a");
    CHECK((!fixed_array<Entity, 3>::try_from(all).has_value()));

    // [...a, x] en colonnes.
    const auto more = make_soa<EntityLayout>(spread(all), Entity{string("c"), {0.0, 0.0}, 7}, spread(plain));
    CHECK(more.size() == 5 && more[2].name == "c" && more[4].name == "b");

    // Parcours et texte.
    std::int32_t total = 0;
    for (const auto& entity : all) total += entity.mass;
    CHECK(total == 9);
    CHECK_TEXT(interpolate(all), "[{name: \"a\", mass: 3}, {name: \"b\", mass: 6}]");
}

}  // namespace

int main() {
    test_basics();
    test_conversions();
    test_mutation();
    if (failures == 0) std::printf("soa : tout passe\n");
    return failures == 0 ? 0 : 1;
}
