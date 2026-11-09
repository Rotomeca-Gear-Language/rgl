// Tests du runtime : le résultat d'un guard (rgl::standard::guarded), fail, done, la boucle de guard
// (rgl::standard::repeat), et la lecture d'un
// nombre pour `as!` (parse_number, cast_message). Le programme renvoie 0 si tout passe, 1 sinon, et
// affiche chaque vérification ratée.

#include <cstdint>
#include <cstdio>
#include <variant>

#include "rgl/standard/convert.hpp"
#include "rgl/standard/exception.hpp"
#include "rgl/standard/loop.hpp"

using rgl::standard::guarded;
using rgl::standard::parse_number;
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

// Des molds d'erreur comme ceux que le compilateur génère.
struct error {
    string message;
    string where;
};
struct other_error {
    string message;
    string where;
    std::int32_t code = 0;
};

// Un guard comme ceux que le compilateur produit : la valeur reçue, ou un échec.
guarded<std::int32_t, error> positive(std::int32_t n) {
    if (n < 0) return rgl::standard::fail(error{string("négatif"), string("main.rgl:2:5")});
    return n;
}

guarded<void, error> check(bool ok) {
    if (!ok) return rgl::standard::fail(error{string("échec"), string("main.rgl:7:9")});
    return guarded<void, error>();
}

// Un guard qui peut envoyer deux types d'erreur : son erreur est leur union.
using both = std::variant<error, other_error>;
guarded<std::int32_t, both> either(std::int32_t n) {
    if (n == 0) return rgl::standard::fail(both(std::in_place_index<1>, other_error{string("zéro"), string("main.rgl:9:9"), 7}));
    return n;
}

void test_guarded() {
    const auto good = positive(4);
    CHECK(!good.failed());
    CHECK(good.value() == 4);

    const auto bad = positive(-1);
    CHECK(bad.failed());
    CHECK(bad.error().message == "négatif");
    CHECK(bad.error().where == "main.rgl:2:5");

    CHECK(!check(true).failed());
    CHECK(check(false).failed());
    CHECK(check(false).error().message == "échec");

    // Un guard qui renvoie une chaîne : la valeur et l'échec ne se confondent pas.
    const guarded<string, error> text(string("ok"));
    CHECK(!text.failed() && text.value() == "ok");

    const auto zero = either(0);
    CHECK(zero.failed() && zero.error().index() == 1 && std::get<1>(zero.error()).code == 7);

    // Une valeur sans valeur vide : seule elle est construite.
    struct no_default {
        explicit no_default(std::int32_t v) : value(v) {}
        std::int32_t value;
    };
    const guarded<no_default, error> built(no_default(5));
    const auto copy = built;
    CHECK(!copy.failed() && copy.value().value == 5);
    const guarded<no_default, error> lost(rgl::standard::fail(error{string("non"), string("main.rgl:1:1")}));
    CHECK(lost.failed() && lost.error().message == "non");

    // Un guard qui ne peut pas échouer.
    const guarded<std::int32_t, rgl::standard::no_error> sure(3);
    CHECK(!sure.failed() && sure.value() == 3);
}

// Un guard bouclé : divise par deux jusqu'à 1 (done), échoue sur 0.
guarded<std::int32_t, error> halve(std::int32_t n) {
    if (n == 0) return rgl::standard::fail(error{string("zéro"), string("main.rgl:3:9")});
    if (n == 1) return rgl::standard::done(n);
    return n / 2;
}

void test_done() {
    const auto finished = halve(1);
    CHECK(!finished.failed() && finished.finished() && finished.value() == 1);
    const auto going = halve(8);
    CHECK(!going.failed() && !going.finished() && going.value() == 4);
    const auto copy = finished;
    CHECK(copy.finished());
}

void test_repeat() {
    const auto step = [](const std::int32_t& n) { return halve(n); };

    // Sans limite : jusqu'au done.
    const auto all = rgl::standard::repeat<std::int32_t>(step, 64, 0);
    CHECK(!all.failed() && !all.exhausted() && all.value() == 1);

    // Limite atteinte : la dernière valeur renvoyée.
    const auto three = rgl::standard::repeat<std::int32_t>(step, 64, 3);
    CHECK(!three.failed() && three.exhausted() && three.value() == 8);

    // Le done au dernier tour permis n'épuise pas la limite.
    const auto exact = rgl::standard::repeat<std::int32_t>(step, 4, 3);
    CHECK(!exact.exhausted() && exact.value() == 1);

    // Un fail arrête la boucle.
    const auto failed = rgl::standard::repeat<std::int32_t>(step, 0, 5);
    CHECK(failed.failed() && !failed.exhausted() && failed.error().message == "zéro");

    // Une valeur chaîne : chaque tour reçoit la précédente.
    const auto shorten = [](const string& text) -> guarded<string, error> {
        if (text.size() <= 2) return rgl::standard::done(text);
        return string(text.data() + 1, text.size() - 1);
    };
    const auto shortened = rgl::standard::repeat<string>(shorten, string("abcdef"), 0);
    CHECK(shortened.value() == "ef");
}

template <class T>
bool parses(const char* text, T expected) {
    T value{};
    return parse_number(string(text), value) && value == expected;
}

template <class T>
bool refuses(const char* text) {
    T value{};
    return !parse_number(string(text), value);
}

void test_parse_number() {
    CHECK(parses<std::int32_t>("42", 42));
    CHECK(parses<std::int32_t>("-7", -7));
    CHECK(parses<std::int32_t>("2147483647", 2147483647));
    CHECK(parses<std::int8_t>("-128", static_cast<std::int8_t>(-128)));
    CHECK(parses<std::uint64_t>("18446744073709551615", 18446744073709551615ull));
    CHECK(parses<double>("3.25", 3.25));
    CHECK(parses<double>("1e3", 1000.0));
    CHECK(parses<float>("0.5", 0.5f));

    CHECK(refuses<std::int32_t>(""));
    CHECK(refuses<std::int32_t>("abc"));
    CHECK(refuses<std::int32_t>("12abc"));      // la chaîne entière doit être le nombre
    CHECK(refuses<std::int32_t>(" 12"));        // pas d'espace autour
    CHECK(refuses<std::int32_t>("12 "));
    CHECK(refuses<std::int32_t>("2147483648"));  // hors limites
    CHECK(refuses<std::int8_t>("128"));
    CHECK(refuses<std::uint32_t>("-1"));        // pas de signe pour un non signé
    CHECK(refuses<std::int32_t>("1.5"));
}

void test_try_parse_number() {
    CHECK(rgl::standard::try_parse_number<std::int32_t>(string("42")) == 42);
    CHECK(!rgl::standard::try_parse_number<std::int32_t>(string("abc")).has_value());
    CHECK(!rgl::standard::try_parse_number<std::uint8_t>(string("256")).has_value());
}

void test_cast_message() {
    CHECK(rgl::standard::cast_message(string("abc"), "int") == "« abc » n'est pas un int");
}

}  // namespace

int main() {
    test_guarded();
    test_done();
    test_repeat();
    test_parse_number();
    test_cast_message();
    test_try_parse_number();

    if (failures != 0) std::fprintf(stderr, "%d vérification(s) ratée(s)\n", failures);
    return failures == 0 ? 0 : 1;
}
