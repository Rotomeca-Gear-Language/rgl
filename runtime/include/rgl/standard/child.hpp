#pragma once

// Les processus enfants qui restent ouverts : on les lance, on leur écrit sur l'entrée standard, on lit leur sortie et leur sortie
// d'erreur au fil de l'eau, on attend leur fin ou on les arrête. Contrairement à `process::run`, qui attend la fin et rend tout d'un
// coup, un enfant garde ses trois flux ouverts jusqu'à `release`. Chaque enfant est désigné par un identifiant qui ne vaut que dans ce
// programme. Rien ne s'arrête ni ne lève : un échec est une valeur rendue.
//
// Attention : si l'enfant remplit un flux que personne ne lit (la sortie d'erreur, par exemple) il finit par se bloquer, et l'on se
// bloque à l'attendre ; il faut lire ou fermer les deux flux.
//
// Le chemin de l'exécutable, les arguments et le dossier de travail sont en UTF-8 ; sous Windows ils sont convertis en UTF-16. Un
// exécutable nommé `rgl` ou `rgl.exe` est refusé.

#include <cstdint>
#include <string>
#include <vector>

#include "rgl/standard/string.hpp"

namespace rgl::standard::child {

// Pourquoi un enfant n'a pas pu être lancé : valeurs négatives rendues par `start`.
constexpr std::int64_t not_found = -1;
constexpr std::int64_t denied = -2;
constexpr std::int64_t failed = -3;

// Le flux d'un enfant à lire.
enum class stream { out = 0, err = 1 };

// Le résultat d'une lecture : des octets lus, le délai passé sans rien lire, ou le flux fermé (et vidé).
enum class read_status { data, timeout, closed };

// Lance `program` avec `args` (sans le nom du programme) dans `cwd` (vide : le dossier de ce programme). Rend un identifiant positif, ou
// `not_found`, `denied`, `failed`.
std::int64_t start(const string& program, const std::vector<string>& args, const string& cwd);

// Écrit sur l'entrée standard de l'enfant ; faux si elle est fermée ou si l'écriture échoue.
bool write(std::int64_t id, const char* data, std::size_t size);

// Ferme l'entrée standard de l'enfant : il voit la fin de son entrée.
void close_input(std::int64_t id);

// Lit au plus `limit` octets disponibles sur le flux, en attendant au plus `timeout_ms` millisecondes (0 : sans limite) qu'il y en ait.
read_status read(std::int64_t id, stream which, std::uint32_t timeout_ms, std::size_t limit, std::string& into);

// Lit une ligne complète (sans son `\n` ni son `\r`), en attendant au plus `timeout_ms` millisecondes ; à la fermeture du flux, ce qui
// reste sans retour à la ligne est rendu comme dernière ligne, puis le flux est `closed`.
read_status read_line(std::int64_t id, stream which, std::uint32_t timeout_ms, std::string& into);

// Attend la fin de l'enfant au plus `timeout_ms` millisecondes (0 : sans limite). Vrai s'il est fini, avec son code de sortie.
bool wait(std::int64_t id, std::uint32_t timeout_ms, std::int32_t& exit_code);

// Arrête l'enfant sans lui laisser le temps de finir.
void kill(std::int64_t id);

// Des octets lus en `string` : chaque octet qui n'est pas de l'UTF-8 devient « U+FFFD ». Un caractère coupé entre deux lectures par morceaux
// est donc abîmé ; les lectures par lignes, elles, ne coupent jamais un caractère.
string to_text(const std::string& data);

// Oublie l'enfant et ferme ses flux ; l'identifiant ne vaut plus rien.
void release(std::int64_t id);

}  // namespace rgl::standard::child
