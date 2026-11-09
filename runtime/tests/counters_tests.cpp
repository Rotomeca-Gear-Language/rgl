// Programme de test des compteurs de mémoire (compilé avec -DRGL_COUNT_ALLOCATIONS) : fait des blocs et des copies connus ;
// le test C# lit le bilan que le programme écrit en s'arrêtant sur la sortie d'erreur.

#include <cstdint>

#include "rgl/standard/array.hpp"
#include "rgl/standard/string.hpp"

int main() {
    rgl::standard::array<std::int32_t> numbers;
    for (std::int32_t i = 0; i < 10; ++i) numbers.push(i);
    const rgl::standard::array<std::int32_t> copy = numbers;   // une copie profonde de 10 éléments
    rgl::standard::mstring text;
    text.append(rgl::standard::string("un texte assez long pour ne pas tenir dans l'objet lui-même", 61));
    const rgl::standard::mstring text_copy = text;             // une copie de 61 octets
    return copy.size() == 10 && text_copy.size() == 61 ? 0 : 1;
}
