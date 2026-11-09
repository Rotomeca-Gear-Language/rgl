#pragma once

// Le terminal, les signaux et l'observation de fichiers : ce qu'un programme interactif ou de longue durée demande au système.
//
// Terminal : une saisie sans écho (`read_password`), la lecture d'une touche à la fois (`read_key`, flèches et touches de fonction
// comprises) et la taille de la fenêtre. Quand l'entrée n'est pas un terminal (fichier, tube), `read_key` dit qu'elle n'est pas
// disponible et `read_password` lit une ligne ordinaire. Sous Windows, l'écriture de séquences ANSI demande `enable_ansi`.
//
// Signaux : `install_signal` demande à garder un signal (`interrupt`, `terminate`, et sous POSIX `hangup`, `user1`, `user2`) au lieu de
// laisser le système arrêter le programme ; `take_signal` rend celui qui est arrivé, `wait_signal` l'attend.
//
// Observation : `wait_for_change` rend ce qui a changé sous un fichier ou un dossier, en comparant des instantanés (taille et date de
// modification) toutes les 50 millisecondes : elle marche partout, sans API propre à une plateforme.

#include <cstdint>
#include <vector>

#include "rgl/standard/string.hpp"

namespace rgl::standard::system {

// Vrai si l'entrée standard est un terminal.
bool is_terminal_input();

// Vrai si la sortie standard est un terminal.
bool is_terminal_output();

// La taille de la fenêtre du terminal, en colonnes et en lignes (80 sur 24 si elle est inconnue).
void terminal_size(std::int32_t& columns, std::int32_t& rows);

// Autorise les séquences ANSI sur la sortie (Windows ; ailleurs, vrai pour un terminal).
bool enable_ansi();

// Lit une ligne sans l'écrire à l'écran ; sans terminal, une ligne ordinaire. Le retour à la ligne est retiré.
string read_password();

// Attend une touche au plus `timeout_ms` millisecondes (0 : sans limite) et rend son nom : `up`, `down`, `left`, `right`, `home`, `end`,
// `pageup`, `pagedown`, `delete`, `enter`, `tab`, `escape`, `backspace`, ou le caractère écrit en UTF-8. Le texte vide dit que le délai est
// passé. `available` est faux quand l'entrée n'est pas un terminal.
string read_key(std::uint32_t timeout_ms, bool& available);

// Un menu à choisir aux flèches : le titre, puis une ligne par option, la choisie en vidéo inverse. Haut et bas déplacent le choix,
// `home` et `end` vont aux extrémités, un chiffre de 1 à 9 va à l'option de ce rang, `enter` valide, `escape` abandonne. Rend le rang
// choisi, ou -1 si l'utilisateur abandonne ou s'il n'y a pas de terminal (l'appelant demande alors un numéro à la ligne).
std::int32_t menu(const string& title, const std::vector<string>& options, std::int32_t selected);

// Garde le signal `name` ; faux s'il n'existe pas sur cette plateforme.
bool install_signal(const string& name);

// Le nom du premier signal gardé qui est arrivé, retiré de la file ; le texte vide s'il n'en est pas arrivé.
string take_signal();

// Comme `take_signal`, en attendant au plus `timeout_ms` millisecondes (0 : sans limite).
string wait_signal(std::uint32_t timeout_ms);

// Attend un changement sous `path` (un fichier ou un dossier, parcouru en profondeur) au plus `timeout_ms` millisecondes (0 : sans limite).
// Rend une ligne `created chemin`, `modified chemin` ou `removed chemin` par changement ; vide si le délai est passé.
std::vector<string> wait_for_change(const string& path, std::uint32_t timeout_ms);

}  // namespace rgl::standard::system
