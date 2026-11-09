// Tests du runtime : les processus enfants qui restent ouverts (child.hpp). Le programme renvoie 0 si tout passe, 1 sinon, et affiche
// chaque vérification ratée. Les commandes sont celles de l'interpréteur du système (`cmd` sous Windows, `sh` ailleurs).

#include <cstdio>
#include <string>
#include <vector>

#include "rgl/standard/child.hpp"

using rgl::standard::string;
namespace child = rgl::standard::child;

namespace {

int failures = 0;

#define CHECK(condition)                                                                        \
    do {                                                                                        \
        if (!(condition)) {                                                                     \
            std::fprintf(stderr, "ÉCHEC %s:%d : %s\n", __FILE__, __LINE__, #condition);         \
            ++failures;                                                                         \
        }                                                                                       \
    } while (false)

std::string trimmed(std::string value) {
    while (!value.empty() && (value.back() == ' ' || value.back() == '\r' || value.back() == '\n')) value.pop_back();
    return value;
}

#if defined(_WIN32)
const string echo_program("findstr");
const std::vector<string> echo_args{string("^")};
const string shell("cmd");
const std::vector<string> both_args{string("/c"), string("echo out& echo err 1>&2")};
const string sleeper("ping");
const std::vector<string> sleeper_args{string("-n"), string("30"), string("127.0.0.1")};
#else
const string echo_program("cat");
const std::vector<string> echo_args{};
const string shell("sh");
const std::vector<string> both_args{string("-c"), string("echo out; echo err 1>&2")};
const string sleeper("sleep");
const std::vector<string> sleeper_args{string("30")};
#endif

}  // namespace

int main() {
    // Une entrée écrite, une sortie lue ligne à ligne, la fin des deux.
    const auto echo = child::start(echo_program, echo_args, string());
    CHECK(echo > 0);
    CHECK(child::write(echo, "bonjour\nmonde\n", 14));
    child::close_input(echo);
    std::string line;
    CHECK(child::read_line(echo, child::stream::out, 5000, line) == child::read_status::data);
    CHECK(trimmed(line) == "bonjour");
    CHECK(child::read_line(echo, child::stream::out, 5000, line) == child::read_status::data);
    CHECK(trimmed(line) == "monde");
    CHECK(child::read_line(echo, child::stream::out, 5000, line) == child::read_status::closed);
    std::int32_t code = -1;
    CHECK(child::wait(echo, 5000, code));
    CHECK(code == 0);
    CHECK(!child::write(echo, "x", 1) || true);
    child::release(echo);

    // Les deux flux séparés.
    const auto both = child::start(shell, both_args, string());
    CHECK(both > 0);
    CHECK(child::read_line(both, child::stream::out, 5000, line) == child::read_status::data);
    CHECK(trimmed(line) == "out");
    CHECK(child::read_line(both, child::stream::err, 5000, line) == child::read_status::data);
    CHECK(trimmed(line) == "err");
    CHECK(child::wait(both, 5000, code));
    std::string rest;
    CHECK(child::read(both, child::stream::out, 500, 100, rest) == child::read_status::closed);
    child::release(both);

    // Un programme qui n'existe pas, et le compilateur refusé.
    CHECK(child::start(string("ce-programme-n-existe-pas-xyz"), {}, string()) == child::not_found);
    CHECK(child::start(string("/chemin/rgl"), {}, string()) == child::denied);
    CHECK(child::start(string("C:\\outils\\RGL.EXE"), {}, string()) == child::denied);

    // Un enfant qui dort : la lecture passe son délai, l'attente aussi, puis l'arrêt le termine.
    const auto sleeping = child::start(sleeper, sleeper_args, string());
    CHECK(sleeping > 0);
    std::string nothing;
    CHECK(child::read(sleeping, child::stream::err, 100, 100, nothing) == child::read_status::timeout);
    CHECK(!child::wait(sleeping, 100, code));
    child::kill(sleeping);
    CHECK(child::wait(sleeping, 5000, code));
    child::release(sleeping);

    // Un identifiant inconnu ne fait rien.
    CHECK(!child::write(9999, "x", 1));
    CHECK(child::read(9999, child::stream::out, 10, 10, nothing) == child::read_status::closed);
    CHECK(!child::wait(9999, 10, code));
    child::kill(9999);
    child::release(9999);

    if (failures == 0) std::puts("enfants : tout passe");
    return failures == 0 ? 0 : 1;
}
