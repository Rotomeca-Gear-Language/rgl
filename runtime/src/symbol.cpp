// La table des symboles : les entrées internées à l'exécution, et les littéraux inscrits.
// Table de hachage à adressage ouvert (sondage linéaire), protégée par un verrou lecteurs-écrivain :
// les recherches, de loin les plus fréquentes, se font en parallèle.

#include "rgl/standard/symbol.hpp"

#include <cstdint>
#include <cstring>
#include <mutex>
#include <new>
#include <shared_mutex>

#include "rgl/standard/memory.hpp"

namespace rgl::standard::primitives {

namespace {

struct Table {
    std::shared_mutex mutex;
    const symbol_entry** slots = nullptr;  // capacity cases, nullptr = libre
    std::size_t capacity = 0;              // puissance de deux
    std::size_t count = 0;
};

// Jamais détruite : un symbole peut servir jusqu'à la toute fin du programme, destructeurs
// statiques compris. Créée au premier appel, donc avant tout usage, quel que soit l'ordre
// d'initialisation des variables globales.
Table& table() {
    static Table* const instance = new (allocate_or_abort(heap_allocator(), sizeof(Table))) Table();
    return *instance;
}

// FNV-1a sur 64 bits.
std::size_t hash(const char* name, std::size_t size) noexcept {
    std::uint64_t value = 14695981039346656037ull;
    for (std::size_t i = 0; i < size; ++i) {
        value ^= static_cast<unsigned char>(name[i]);
        value *= 1099511628211ull;
    }
    return static_cast<std::size_t>(value);
}

bool same_name(const symbol_entry& entry, const char* name, std::size_t size) noexcept {
    return entry.size == size && (size == 0 || std::memcmp(entry.name, name, size) == 0);
}

// La case de ce nom : celle qui le contient, ou la case libre où l'insérer. La table n'est
// jamais pleine (au plus à moitié), la boucle s'arrête donc toujours.
const symbol_entry** find_slot(const Table& t, const char* name, std::size_t size) noexcept {
    const std::size_t mask = t.capacity - 1;
    for (std::size_t i = hash(name, size) & mask;; i = (i + 1) & mask) {
        const symbol_entry** slot = &t.slots[i];
        if (*slot == nullptr || same_name(**slot, name, size)) return slot;
    }
}

const symbol_entry* find(const Table& t, const char* name, std::size_t size) noexcept {
    return t.capacity == 0 ? nullptr : *find_slot(t, name, size);
}

// Ajoute une entrée absente de la table, en doublant sa taille au-delà de la moitié remplie.
// Verrou exclusif déjà pris.
void insert(Table& t, const symbol_entry* entry) {
    if ((t.count + 1) * 2 > t.capacity) {
        const std::size_t capacity = t.capacity == 0 ? 64 : t.capacity * 2;
        const AbiAllocator& allocator = heap_allocator();
        auto** slots = static_cast<const symbol_entry**>(allocate_or_abort(allocator, capacity * sizeof(const symbol_entry*)));
        for (std::size_t i = 0; i < capacity; ++i) slots[i] = nullptr;

        Table grown{};
        grown.slots = slots;
        grown.capacity = capacity;
        for (std::size_t i = 0; i < t.capacity; ++i) {
            if (t.slots[i] != nullptr) *find_slot(grown, t.slots[i]->name, t.slots[i]->size) = t.slots[i];
        }

        if (t.slots != nullptr) release_block(allocator, static_cast<void*>(t.slots));
        t.slots = slots;
        t.capacity = capacity;
    }

    *find_slot(t, entry->name, entry->size) = entry;
    ++t.count;
}

}  // namespace

symbol symbol::intern(const char* name, std::size_t size) {
    Table& t = table();
    {
        std::shared_lock lock(t.mutex);
        if (const symbol_entry* entry = find(t, name, size)) return symbol(*entry);
    }

    std::unique_lock lock(t.mutex);
    // Un autre thread a pu le créer entre les deux verrous.
    if (const symbol_entry* entry = find(t, name, size)) return symbol(*entry);

    // L'entrée et son nom dans un seul bloc, jamais libéré.
    void* block = allocate_or_abort(heap_allocator(), sizeof(symbol_entry) + size + 1);
    char* text = static_cast<char*>(block) + sizeof(symbol_entry);
    if (size != 0) std::memcpy(text, name, size);
    text[size] = '\0';
    auto* entry = new (block) symbol_entry{text, size};

    insert(t, entry);
    return symbol(*entry);
}

void symbol::register_literal(const symbol_entry& entry) {
    Table& t = table();
    std::unique_lock lock(t.mutex);
    if (find(t, entry.name, entry.size) == nullptr) insert(t, &entry);
}

}  // namespace rgl::standard::primitives
