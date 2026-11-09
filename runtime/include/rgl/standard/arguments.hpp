#pragma once

#include "rgl/standard/array.hpp"
#include "rgl/standard/string.hpp"

namespace rgl::standard {

// Arguments de la ligne de commande, sans le nom du programme, en UTF-8 : le [string] args du
// train d'entrée. Le main() généré l'appelle seulement si le train déclare ce paramètre.
//
// Sous Windows, le argv de main() est encodé dans la page de code du système, pas en UTF-8 :
// la ligne de commande est relue en UTF-16 puis convertie (lier avec -lshell32). Sous Linux, les
// octets d'argv sont repris tels quels.
array<string> read_arguments(int argc, char** argv);

}  // namespace rgl::standard
