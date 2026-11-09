#pragma once

// rgl::standard::symbol : le type RGL symbol, une valeur internée (§pret) comparée par identité.
//
// Un symbole est une poignée légère : un pointeur vers l'entrée unique de son nom. Le copier ou
// le comparer revient à copier ou comparer un pointeur, sans allocation ni opération atomique.
//
// Deux façons d'obtenir une entrée :
// - un littéral §nom du source : le compilateur génère une entrée statique, constexpr
//   (`inline constexpr symbol_entry rgl_s_pret{"pret", 4};`), unique dans tout le programme
//   grâce à `inline` ; elle ne coûte ni allocation ni verrou ;
// - un nom connu seulement à l'exécution : symbol::intern cherche l'entrée de ce nom dans la
//   table des symboles, ou en crée une. Une entrée créée ainsi vit jusqu'à la fin du programme.
//
// Pour qu'intern et les littéraux désignent la même entrée pour un même nom, le programme
// enregistre ses littéraux (register_literal) avant son premier appel à intern. Un programme qui
// n'appelle jamais intern n'a besoin ni de la table, ni de symbol.cpp.

#include <cstddef>

namespace rgl::standard {

namespace primitives {

// L'entrée unique d'un nom de symbole. name n'est pas forcément terminé par '\0' : sa longueur
// est size. Une entrée ne bouge jamais et n'est jamais libérée.
struct symbol_entry {
    const char* name;
    std::size_t size;
};

class symbol {
   public:
    // Le symbole d'une entrée : un littéral §nom, ou une entrée de la table.
    constexpr explicit symbol(const symbol_entry& entry) noexcept : entry_(&entry) {}

    constexpr const char* data() const noexcept { return entry_->name; }
    constexpr std::size_t size() const noexcept { return entry_->size; }

    // Identité : deux symboles sont égaux s'ils désignent la même entrée, donc le même nom.
    friend constexpr bool operator==(symbol left, symbol right) noexcept { return left.entry_ == right.entry_; }

    // Le symbole de ce nom : son entrée dans la table, créée au premier appel. Sûr entre threads.
    static symbol intern(const char* name, std::size_t size);

    // Inscrit l'entrée d'un littéral dans la table, pour qu'intern la renvoie pour ce nom. Si le
    // nom y est déjà, l'entrée existante est gardée : à appeler avant le premier intern de ce nom.
    static void register_literal(const symbol_entry& entry);

   private:
    const symbol_entry* entry_;
};

}  // namespace primitives

using primitives::symbol;
using primitives::symbol_entry;

}  // namespace rgl::standard
