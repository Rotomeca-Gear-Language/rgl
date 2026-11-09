#pragma once

// XML : `var n <- xml "a.xml";` lit un document en `XmlNode` et `n -> xml "b.xml";` l'écrit. Le runtime lit le
// fichier en un arbre neutre (`element`, analyseur de xml.cpp) et l'écrit depuis lui ; le compilateur écrit les
// deux fonctions qui passent de cet arbre au mold `XmlNode` de la std et inversement (le struct est généré).
//
// Le sous-ensemble pris en charge : éléments, attributs, texte, CDATA, commentaires et instructions de traitement
// (ignorés), les cinq entités prédéfinies et les références numériques. Une balise DOCTYPE est refusée (pas de DTD,
// donc pas d'entité externe ni d'explosion d'entités). Les espaces de noms ne sont pas résolus : un nom garde son
// préfixe, `xmlns:…` est un attribut. Aucune exception : chaque fonction rend faux et note son message dans `failure`.

#include <cstddef>
#include <string>
#include <utility>
#include <vector>

#include "rgl/standard/file.hpp"
#include "rgl/standard/string.hpp"

namespace rgl::standard::xml {

// Profondeur maximale d'un document : au-delà, l'analyseur refuse.
inline constexpr int max_depth = 256;

struct element {
    string name;
    // Les attributs dans l'ordre du document.
    std::vector<std::pair<string, string>> attributes;
    std::vector<element> children;
    // Le texte directement dans l'élément, bout à bout ; vide s'il n'y a que des espaces et des fils.
    string text;
};

// Le message de la dernière erreur.
struct failure {
    string message;
};

// Le résultat d'une lecture ou d'une écriture : réussi, échec du fichier (`io_status`), ou échec du contenu
// (le message est dans le `failure`).
struct outcome {
    bool ok;
    bool io;
    file::status io_status;
};

// Analyse un document ; faux avec la ligne et la colonne de l'erreur.
bool parse(const string& text, element& root, failure& error);

// Écrit un document, indenté de deux espaces, précédé de la déclaration `<?xml version="1.0" encoding="UTF-8"?>` ;
// faux si un nom de balise ou d'attribut n'en est pas un.
bool serialize(const element& root, mstring& out, failure& error);

// Lit le fichier puis l'analyse.
inline outcome read(const string& path, element& root, failure& error) {
    string text;
    if (const auto status = file::read_text(path, text); status != file::status::ok) return {false, true, status};
    if (!parse(text, root, error)) return {false, false, file::status::ok};
    return {true, false, file::status::ok};
}

// Écrit le texte, suivi d'un retour à la ligne, dans le fichier.
inline outcome write_file(const string& path, mstring& text) {
    text.push_back('\n');
    const auto status = file::write(path, text.data(), text.size());
    return {status == file::status::ok, status != file::status::ok, status};
}

}  // namespace rgl::standard::xml
