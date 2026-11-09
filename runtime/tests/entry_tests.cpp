// Test du composant « arguments » : un main() comme celui que génère le compilateur pour un train
// qui déclare [string] args. Écrit chaque argument reçu sur sa ligne (octets UTF-8 bruts) puis
// renvoie 40 + leur nombre : le test vérifie la lecture des arguments, leur encodage et la remontée
// du code de sortie.

#include <cstdint>
#include <cstdio>

#include "rgl/standard/arguments.hpp"

static std::uint32_t rgl_entry_train(const rgl::standard::array<rgl::standard::string>& args) {
    for (const auto& arg : args) {
        std::fwrite(arg.data(), 1, arg.size(), stdout);
        std::fputc('\n', stdout);
    }
    std::fflush(stdout);
    return 40u + static_cast<std::uint32_t>(args.size());
}

int main(int argc, char** argv) { return static_cast<int>(rgl_entry_train(rgl::standard::read_arguments(argc, argv))); }
