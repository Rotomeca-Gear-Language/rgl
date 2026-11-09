// Tests du runtime : les processus enfants (process.hpp). Le programme reçoit en argument un dossier qui existe ; il
// renvoie 0 si tout passe, 1 sinon, et affiche chaque vérification ratée. Les commandes sont celles de l'interpréteur du
// système (`cmd` sous Windows, `sh` ailleurs).

#include <chrono>
#include <cstdio>
#include <string>

#include "rgl/standard/process.hpp"

using rgl::standard::string;
namespace process = rgl::standard::process;

namespace {

int failures = 0;

#define CHECK(condition)                                                                        \
    do {                                                                                        \
        if (!(condition)) {                                                                     \
            std::fprintf(stderr, "ÉCHEC %s:%d : %s\n", __FILE__, __LINE__, #condition);         \
            ++failures;                                                                         \
        }                                                                                       \
    } while (false)

#if defined(_WIN32)
const string shell("cmd");

std::vector<string> command(const std::string& what) {
    if (what == "echo") return {string("/c"), string("echo"), string("hi")};
    if (what == "exit") return {string("/c"), string("exit"), string("3")};
    if (what == "stderr") return {string("/c"), string("echo"), string("oops"), string("1>&2")};
    if (what == "env") return {string("/c"), string("echo"), string("%RGL_VAR%")};
    if (what == "cwd") return {string("/c"), string("cd")};
    return {string("/c"), string("ping"), string("-n"), string("6"), string("127.0.0.1"), string(">nul")};   // sleep : environ 5 secondes
}
#else
const string shell("/bin/sh");

std::vector<string> command(const std::string& what) {
    if (what == "echo") return {string("-c"), string("echo hi")};
    if (what == "exit") return {string("-c"), string("exit 3")};
    if (what == "stderr") return {string("-c"), string("echo oops 1>&2")};
    if (what == "env") return {string("-c"), string("echo $RGL_VAR")};
    if (what == "cwd") return {string("-c"), string("pwd")};
    return {string("-c"), string("sleep 5")};
}
#endif

bool starts_with(const string& text, const char* prefix) {
    const std::string value(text.data(), text.size());
    return value.rfind(prefix, 0) == 0;
}

void test_output_and_exit_code() {
    auto result = process::run(shell, command("echo"), process::options{});
    CHECK(result.error == process::failure::none && result.exit_code == 0);
    CHECK(starts_with(result.out, "hi") && result.err.size() == 0);

    result = process::run(shell, command("exit"), process::options{});
    CHECK(result.error == process::failure::none && result.exit_code == 3);

    result = process::run(shell, command("stderr"), process::options{});
    CHECK(result.exit_code == 0 && starts_with(result.err, "oops") && result.out.size() == 0);

    // Une sortie jetée n'est pas dans le résultat.
    process::options quiet;
    quiet.out = process::output::null;
    result = process::run(shell, command("echo"), quiet);
    CHECK(result.exit_code == 0 && result.out.size() == 0);
}

void test_environment_and_directory(const string& directory) {
    process::options with_env;
    with_env.env.emplace_back(string("RGL_VAR"), string("valeur-env"));
    auto result = process::run(shell, command("env"), with_env);
    CHECK(starts_with(result.out, "valeur-env"));

    process::options in_directory;
    in_directory.cwd = directory;
    result = process::run(shell, command("cwd"), in_directory);
    CHECK(result.error == process::failure::none && result.out.size() > 0);
}

void test_errors() {
    auto result = process::run(string("rgl-programme-qui-n-existe-pas"), {}, process::options{});
    CHECK(result.error == process::failure::exec_not_found && result.message.size() > 0);

    process::options in_missing;
    in_missing.cwd = string("dossier-qui-n-existe-pas-du-tout");
    result = process::run(shell, command("echo"), in_missing);
    CHECK(result.error != process::failure::none);

    // Un programme RGL ne relance pas rgl, quel que soit le chemin ou la casse.
    for (const char* name : {"rgl", "RGL.EXE", "outils/rgl.exe", "C:\\bin\\Rgl"}) {
        result = process::run(string(name), {}, process::options{});
        CHECK(result.error == process::failure::permission_denied);
    }
    CHECK(process::run(string(), {}, process::options{}).error == process::failure::exec_not_found);
}

void test_timeout() {
    process::options limited;
    limited.timeout_ms = 300;
    const auto start = std::chrono::steady_clock::now();
    const auto result = process::run(shell, command("sleep"), limited);
    const auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - start).count();
    CHECK(result.error == process::failure::timeout);
    CHECK(elapsed < 4000);
}

void test_arguments_with_spaces_and_quotes() {
#if defined(_WIN32)
    // cmd lit sa propre ligne de commande : on vérifie seulement qu'un argument avec des espaces arrive d'un bloc.
    auto result = process::run(shell, {string("/c"), string("echo"), string("a b")}, process::options{});
#else
    auto result = process::run(shell, {string("-c"), string("printf '%s' \"$0\""), string("a \"b\" c")}, process::options{});
#endif
    CHECK(result.exit_code == 0);
    CHECK(result.out.size() > 0);
}

}  // namespace

int main(int argc, char** argv) {
    if (argc < 2) {
        std::fprintf(stderr, "dossier attendu\n");
        return 2;
    }
    test_output_and_exit_code();
    test_environment_and_directory(string(argv[1]));
    test_errors();
    test_timeout();
    test_arguments_with_spaces_and_quotes();
    return failures == 0 ? 0 : 1;
}
