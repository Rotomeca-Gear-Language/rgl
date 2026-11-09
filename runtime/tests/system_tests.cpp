// Tests du runtime : le terminal, les signaux et l'observation de fichiers (system.hpp). Le programme reçoit en argument un dossier
// qui existe ; il renvoie 0 si tout passe, 1 sinon, et affiche chaque vérification ratée.

#include <chrono>
#include <csignal>
#include <cstdio>
#include <fstream>
#include <string>
#include <thread>
#include <vector>

#include "rgl/standard/system.hpp"

using rgl::standard::string;
namespace sys = rgl::standard::system;

namespace {

int failures = 0;

#define CHECK(condition)                                                                        \
    do {                                                                                        \
        if (!(condition)) {                                                                     \
            std::fprintf(stderr, "ÉCHEC %s:%d : %s\n", __FILE__, __LINE__, #condition);         \
            ++failures;                                                                         \
        }                                                                                       \
    } while (false)

std::string text(const string& value) { return std::string(value.data(), value.size()); }

void write_file(const std::string& path, const std::string& content) {
    std::ofstream file(path, std::ios::binary | std::ios::trunc);
    file << content;
}

bool has_change(const std::vector<string>& changes, const std::string& expected_start, const std::string& expected_end) {
    for (const auto& change : changes) {
        const auto line = text(change);
        if (line.rfind(expected_start, 0) == 0 && line.size() >= expected_end.size() &&
            line.compare(line.size() - expected_end.size(), expected_end.size(), expected_end) == 0)
            return true;
    }
    return false;
}

}  // namespace

int main(int argc, char** argv) {
    const std::string folder = argc > 1 ? argv[1] : ".";

    // Signaux.
    CHECK(text(sys::take_signal()).empty());
    CHECK(sys::install_signal(string("interrupt")));
    CHECK(sys::install_signal(string("terminate")));
    CHECK(!sys::install_signal(string("n'existe-pas")));
    std::raise(SIGINT);
    CHECK(text(sys::take_signal()) == "interrupt");
    CHECK(text(sys::take_signal()).empty());
    std::raise(SIGTERM);
    CHECK(text(sys::wait_signal(500)) == "terminate");
    CHECK(text(sys::wait_signal(60)).empty());

    // Terminal : sans terminal, pas de touche ni de menu ; la taille a toujours une valeur.
    std::int32_t columns = 0, rows = 0;
    sys::terminal_size(columns, rows);
    CHECK(columns > 0 && rows > 0);
    if (!sys::is_terminal_input()) {
        bool available = true;
        CHECK(text(sys::read_key(10, available)).empty());
        CHECK(!available);
        CHECK(sys::menu(string("titre"), {string("a"), string("b")}, 0) == -1);
    }
    CHECK(sys::menu(string("titre"), {}, 0) == -1);

    // Observation : un fichier créé, modifié puis supprimé sous un dossier.
    const std::string directory = folder + "/watch";
    std::vector<string> none = sys::wait_for_change(string(folder.c_str(), folder.size()), 120);
    CHECK(none.empty());

    std::thread creator([&] {
        std::this_thread::sleep_for(std::chrono::milliseconds(900));
        write_file(folder + "/a.txt", "un");
    });
    auto created = sys::wait_for_change(string(folder.c_str(), folder.size()), 5000);
    creator.join();
    CHECK(has_change(created, "created ", "/a.txt"));

    std::thread modifier([&] {
        std::this_thread::sleep_for(std::chrono::milliseconds(900));
        write_file(folder + "/a.txt", "deux lignes plus longues");
    });
    auto modified = sys::wait_for_change(string((folder + "/a.txt").c_str(), (folder + "/a.txt").size()), 5000);
    modifier.join();
    CHECK(has_change(modified, "modified ", "/a.txt"));

    std::thread remover([&] {
        std::this_thread::sleep_for(std::chrono::milliseconds(900));
        std::remove((folder + "/a.txt").c_str());
    });
    auto removed = sys::wait_for_change(string(folder.c_str(), folder.size()), 5000);
    remover.join();
    CHECK(has_change(removed, "removed ", "/a.txt"));

    (void)directory;
    if (failures == 0) std::puts("système : tout passe");
    return failures == 0 ? 0 : 1;
}
