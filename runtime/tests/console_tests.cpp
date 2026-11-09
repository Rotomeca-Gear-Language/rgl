// Tests du runtime : la console. Le programme lit toutes les lignes de son entrée et les réécrit
// entre crochets, une par ligne ; le test C# lui donne une entrée et vérifie sa sortie (fins de
// ligne « \n » et « \r\n », ligne vide, dernière ligne sans retour, UTF-8).

#include "rgl/standard/console.hpp"

using rgl::standard::string;

int main() {
    // Une lecture après la fin de l'entrée donne une chaîne vide : on s'arrête sur deux vides de suite.
    int empty = 0;
    while (empty < 2) {
        const string line = rgl::standard::console::read_line();
        empty = line.empty() ? empty + 1 : 0;
        rgl::standard::console::write(string("["));
        rgl::standard::console::write(line);
        rgl::standard::console::write(string("]\n"));
    }
    return 0;
}
