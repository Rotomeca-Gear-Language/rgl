// Tests du runtime : les conversions numériques (fits, checked_cast, try_cast) et les types bornés
// (in_bounds, checked_bounds, try_bounds). Le programme renvoie 0 si tout passe, 1 sinon, et
// affiche chaque vérification ratée.

#include <cstdint>
#include <cstdio>
#include <limits>

#include "rgl/standard/cast.hpp"

using rgl::standard::fits;
using rgl::standard::try_bounds;
using rgl::standard::try_cast;

namespace {

int failures = 0;

#define CHECK(condition)                                                                        \
    do {                                                                                        \
        if (!(condition)) {                                                                     \
            std::fprintf(stderr, "ÉCHEC %s:%d : %s\n", __FILE__, __LINE__, #condition);         \
            ++failures;                                                                         \
        }                                                                                       \
    } while (false)

void test_integers() {
    CHECK(fits<std::int8_t>(127));
    CHECK(!fits<std::int8_t>(128));
    CHECK(fits<std::int8_t>(-128));
    CHECK(!fits<std::int8_t>(-129));
    CHECK(!fits<std::uint32_t>(-1));
    CHECK(fits<std::int64_t>(std::numeric_limits<std::uint64_t>::max() / 2));
    CHECK(!fits<std::int64_t>(std::numeric_limits<std::uint64_t>::max()));
    CHECK(fits<std::uint64_t>(std::numeric_limits<std::int64_t>::max()));
    // Toute valeur constante qui tient se calcule à la compilation.
    static_assert(rgl::standard::checked_cast<std::uint8_t>(200, "", "") == 200);
}

void test_floats() {
    CHECK(fits<std::int8_t>(127.9));
    CHECK(!fits<std::int8_t>(128.0));
    CHECK(fits<std::int8_t>(-128.9));
    CHECK(!fits<std::int8_t>(-129.0));
    CHECK(fits<std::uint8_t>(-0.9));
    CHECK(!fits<std::uint8_t>(-1.0));
    CHECK(!fits<std::int32_t>(std::numeric_limits<double>::quiet_NaN()));
    CHECK(!fits<std::int32_t>(std::numeric_limits<double>::infinity()));
    CHECK(fits<std::int32_t>(2147483647.5));
    CHECK(!fits<std::int32_t>(2147483648.0));
    CHECK(fits<std::int32_t>(-2147483648.5));
    CHECK(!fits<std::int32_t>(-2147483649.0));
    // min - 1 n'existe pas en double pour un int64 : -2^63 tient, la valeur d'en dessous non.
    CHECK(fits<std::int64_t>(-9223372036854775808.0));
    CHECK(!fits<std::int64_t>(-9223372036854777856.0));
    CHECK(!fits<std::int64_t>(9223372036854775808.0));
    CHECK(fits<std::uint64_t>(18446744073709549568.0));
    CHECK(!fits<std::uint64_t>(18446744073709551616.0));
    CHECK(!fits<std::int32_t>(3.0e9f));
    CHECK(fits<float>(1.0e300));   // vers un flottant : arrondi, jamais d'échec

    CHECK(rgl::standard::narrow_float<float>(1.0e300) == std::numeric_limits<float>::infinity());
    CHECK(rgl::standard::narrow_float<float>(-1.0e300) == -std::numeric_limits<float>::infinity());
    CHECK(rgl::standard::narrow_float<float>(0.5) == 0.5f);
    CHECK(try_cast<std::int32_t>(-3.7) == std::int32_t{-3});   // tronqué vers zéro
    CHECK(!try_cast<std::uint8_t>(300).has_value());
}

void test_bounds() {
    CHECK(rgl::standard::in_bounds(0, 0, 100));
    CHECK(rgl::standard::in_bounds(100, 0, 100));
    CHECK(!rgl::standard::in_bounds(101, 0, 100));
    CHECK(!rgl::standard::in_bounds(std::numeric_limits<double>::quiet_NaN(), 0.0, 360.0));
    CHECK(try_bounds<std::int32_t>(std::int32_t{50}, 0, 100) == std::int32_t{50});
    CHECK(!try_bounds<std::int32_t>(std::int32_t{150}, 0, 100).has_value());
    CHECK(!try_bounds<std::int32_t>(std::nullopt, 0, 100).has_value());
}

}  // namespace

int main() {
    test_integers();
    test_floats();
    test_bounds();
    return failures == 0 ? 0 : 1;
}
