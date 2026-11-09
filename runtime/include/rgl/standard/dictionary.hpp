#pragma once

// Le dictionnaire [K:V] de RGL : des paires clé-valeur rangées par clé, dans deux tableaux RGL
// (les clés triées, et les valeurs dans le même ordre), contigus en mémoire : plus favorable au cache
// qu'une table de hachage, et alloués par l'allocateur du runtime comme tous les tableaux (ceux d'un pool ou
// d'une arène, quand il y en a un). Les clés ajoutées par add attendent dans un petit tampon, trié lui aussi, que le dictionnaire verse
// d'un coup dans les tableaux principaux quand il dépasse la racine de leur taille (ou avant une lecture qui a besoin de tout l'ordre :
// parcours, comparaison, clés, valeurs) : construire un dictionnaire par n ajouts coûte environ n·√n, et non n². Une lecture
// d'une clé cherche dans les deux. Une lecture qui verse le tampon modifie le dictionnaire : un dictionnaire n'est donc lu que par un
// thread à la fois (les tâches reçoivent des copies). On y ajoute une clé nouvelle (add) ; on n'en retire aucune. La valeur d'une
// clé présente ne se remplace que par une variable mut (accès non const). Lire une clé absente arrête le
// programme (at), comme un index hors limites ; dans un guard, le code généré teste contains et fait échouer
// le guard.
//
// Les clés se comparent par < (entiers, bool, chaînes, enums) ; un symbole, qui n'a qu'une
// identité, est rangé par son nom. Rien ne lève d'exception.

#include <algorithm>
#include <cstddef>
#include <cstdio>
#include <cstdlib>
#include <initializer_list>
#include <string_view>
#include <utility>

#include "rgl/standard/array.hpp"
#include "rgl/standard/nullable.hpp"

namespace rgl::standard {

namespace detail {

// Ordre des clés : < quand le type l'a, sinon le nom (un symbole).
struct key_less {
    template <class K>
    constexpr bool operator()(const K& left, const K& right) const noexcept {
        if constexpr (requires { left < right; }) {
            return left < right;
        } else {
            return std::string_view(left.data(), left.size()) < std::string_view(right.data(), right.size());
        }
    }
};

[[noreturn]] inline void dictionary_failure(const char* what, const char* where) noexcept {
    std::fprintf(stderr, "rgl : %s (%s)\n", what, where);
    std::abort();
}

}  // namespace detail

template <class K, class V>
class dictionary {
   public:
    // Marque des dictionnaires : format.hpp les écrit en texte [clé: valeur, …].
    using rgl_dictionary = V;

    dictionary() = default;

    // Le littéral [k1: v1, k2: v2] : chaque clé une seule fois (le compilateur RGL vérifie les clés
    // connues à la compilation ; sinon, une clé en double arrête le programme). Les paires sont rangées une fois,
    // par un tri, et non insérées une à une.
    dictionary(std::initializer_list<std::pair<K, V>> entries) {
        array<std::pair<K, V>> sorted;
        sorted.reserve(entries.size());
        for (const auto& entry : entries) sorted.push(entry);
        std::sort(sorted.data(), sorted.data() + sorted.size(), [](const auto& left, const auto& right) { return detail::key_less{}(left.first, right.first); });
        _keys.reserve(sorted.size());
        _values.reserve(sorted.size());
        for (std::size_t i = 0; i < sorted.size(); ++i) {
            if (i > 0 && !detail::key_less{}(sorted[i - 1].first, sorted[i].first)) detail::dictionary_failure("clé en double dans un dictionnaire", "littéral");
            _keys.push(std::move(sorted[i].first));
            _values.push(std::move(sorted[i].second));
        }
    }

    // Le dictionnaire des count paires de data, que le compilateur a rangées par clé, sans doublon : une constante de plusieurs milliers d'entrées est une
    // table dans l'exécutable, recopiée ici d'une boucle (au lieu d'une construction par paire dans le code).
    static dictionary from_sorted(const std::pair<K, V>* data, std::size_t count) {
        dictionary result;
        result._keys.reserve(count);
        result._values.reserve(count);
        for (std::size_t i = 0; i < count; ++i) {
            result._keys.push(data[i].first);
            result._values.push(data[i].second);
        }
        return result;
    }

    std::size_t size() const noexcept { return _keys.size() + _pending_keys.size(); }
    bool empty() const noexcept { return size() == 0; }
    bool contains(const K& key) const noexcept { return _lookup(key) != nullptr; }

    // La valeur de key, ou l'arrêt du programme si la clé est absente, avec l'endroit du source RGL.
    const V& at(const K& key, const char* where) const noexcept {
        const V* found = _lookup(key);
        if (!found) detail::dictionary_failure("clé absente du dictionnaire", where);
        return *found;
    }

    // La valeur de key, modifiable, ou l'arrêt du programme si la clé est absente.
    V& at(const K& key, const char* where) noexcept {
        V* found = _lookup(key);
        if (!found) detail::dictionary_failure("clé absente du dictionnaire", where);
        return *found;
    }

    // La valeur de key, sans contrôle : seulement après contains.
    const V& operator[](const K& key) const noexcept { return *_lookup(key); }

    // La valeur de key, modifiable, sans contrôle : seulement après contains.
    V& operator[](const K& key) noexcept { return *_lookup(key); }

    // La valeur de key, ou nullptr si la clé est absente.
    const V* try_at(const K& key) const noexcept { return _lookup(key); }

    // Indique si key peut être ajoutée : elle n'y est pas encore.
    bool can_add(const K& key) const noexcept { return !contains(key); }

    // Ajoute une clé nouvelle, ou arrête le programme si elle y est déjà (remplacer sa valeur passe
    // par at ou operator[], sur une variable mut).
    void add(K key, V value, const char* where) {
        if (_lookup(key)) detail::dictionary_failure("clé déjà présente dans le dictionnaire", where);
        const std::size_t at = _lower_bound(_pending_keys, key);
        _insert(_pending_keys, _pending_values, at, static_cast<K&&>(key), static_cast<V&&>(value));
        // Le tampon est versé quand sa taille au carré dépasse celle des tableaux principaux (plus une marge) : chaque versement coûte la taille
        // du dictionnaire et arrive après ≈ √n ajouts.
        if (_pending_keys.size() * _pending_keys.size() > _keys.size() + 256) _flush();
    }

    // Les clés puis les valeurs, dans l'ordre des clés, rangées dans un tableau RGL (Out : array<K>, soa_array<…>…).
    template <class Out>
    Out keys() const {
        _flush();
        Out result;
        result.reserve(_keys.size());
        for (std::size_t i = 0; i < _keys.size(); ++i) result.push(_keys[i]);
        return result;
    }

    template <class Out>
    Out values() const {
        _flush();
        Out result;
        result.reserve(_values.size());
        for (std::size_t i = 0; i < _values.size(); ++i) result.push(_values[i]);
        return result;
    }

    // Une copie où la clé vaut value (ajoutée ou remplacée).
    dictionary put(K key, V value) const {
        dictionary result = *this;
        result._flush();
        const std::size_t at = _lower_bound(result._keys, key);
        if (at < result._keys.size() && !detail::key_less{}(key, result._keys[at])) result._values[at] = static_cast<V&&>(value);
        else _insert(result._keys, result._values, at, static_cast<K&&>(key), static_cast<V&&>(value));
        return result;
    }

    // Une copie sans la clé donnée.
    dictionary without(const K& key) const {
        _flush();
        dictionary result;
        result._keys.reserve(_keys.size());
        result._values.reserve(_values.size());
        for (std::size_t i = 0; i < _keys.size(); ++i) {
            if (detail::key_less{}(_keys[i], key) || detail::key_less{}(key, _keys[i])) {
                result._keys.push(_keys[i]);
                result._values.push(_values[i]);
            }
        }
        return result;
    }

    // Les paires, rangées par clé : chaque pas rend (clé, valeur) par référence, qu'une liaison `const auto& [clé, valeur]` lit.
    class const_iterator {
       public:
        const_iterator(const dictionary* owner, std::size_t index) noexcept : owner_(owner), index_(index) {}
        std::pair<const K&, const V&> operator*() const noexcept { return {owner_->_keys[index_], owner_->_values[index_]}; }
        const_iterator& operator++() noexcept {
            ++index_;
            return *this;
        }
        bool operator==(const const_iterator& other) const noexcept { return index_ == other.index_; }

       private:
        const dictionary* owner_;
        std::size_t index_;
    };

    const_iterator begin() const noexcept {
        _flush();
        return const_iterator(this, 0);
    }
    const_iterator end() const noexcept { return const_iterator(this, size()); }

    friend bool operator==(const dictionary& left, const dictionary& right) noexcept {
        if (left.size() != right.size()) return false;
        left._flush();
        right._flush();
        for (std::size_t i = 0; i < left._keys.size(); ++i) {
            if (!(left._keys[i] == right._keys[i]) || !(left._values[i] == right._values[i])) return false;
        }
        return true;
    }

   private:
    // Les paires versées, rangées par clé, et le petit tampon des ajouts récents, rangé lui aussi. Versé par une lecture const : mutable.
    mutable array<K> _keys;
    mutable array<V> _values;
    mutable array<K> _pending_keys;
    mutable array<V> _pending_values;

    // Le rang de la première clé de keys qui n'est pas avant key (keys.size() si toutes le sont).
    static std::size_t _lower_bound(const array<K>& keys, const K& key) noexcept {
        std::size_t low = 0;
        std::size_t high = keys.size();
        while (low < high) {
            const std::size_t middle = low + (high - low) / 2;
            if (detail::key_less{}(keys[middle], key)) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    // La valeur de key, dans les tableaux principaux ou dans le tampon ; nullptr si la clé est absente.
    const V* _lookup(const K& key) const noexcept {
        std::size_t at = _lower_bound(_keys, key);
        if (at < _keys.size() && !detail::key_less{}(key, _keys[at])) return &_values[at];
        at = _lower_bound(_pending_keys, key);
        if (at < _pending_keys.size() && !detail::key_less{}(key, _pending_keys[at])) return &_pending_values[at];
        return nullptr;
    }

    V* _lookup(const K& key) noexcept { return const_cast<V*>(static_cast<const dictionary*>(this)->_lookup(key)); }

    // Range une paire au rang at : elle est ajoutée à la fin, puis ramenée à sa place.
    static void _insert(array<K>& keys, array<V>& values, std::size_t at, K key, V value) {
        keys.push(static_cast<K&&>(key));
        values.push(static_cast<V&&>(value));
        std::rotate(keys.data() + at, keys.data() + keys.size() - 1, keys.data() + keys.size());
        std::rotate(values.data() + at, values.data() + values.size() - 1, values.data() + values.size());
    }

    // Verse le tampon dans les tableaux principaux : une fusion de deux suites rangées, dans de nouveaux tableaux du même allocateur.
    void _flush() const {
        if (_pending_keys.size() == 0) return;
        const std::size_t main_size = _keys.size();
        const std::size_t pending_size = _pending_keys.size();
        array<K> keys(_keys.allocator());
        array<V> values(_values.allocator());
        keys.reserve(main_size + pending_size);
        values.reserve(main_size + pending_size);
        std::size_t i = 0;
        std::size_t j = 0;
        while (i < main_size && j < pending_size) {
            if (detail::key_less{}(_pending_keys[j], _keys[i])) {
                keys.push(static_cast<K&&>(_pending_keys[j]));
                values.push(static_cast<V&&>(_pending_values[j]));
                ++j;
            } else {
                keys.push(static_cast<K&&>(_keys[i]));
                values.push(static_cast<V&&>(_values[i]));
                ++i;
            }
        }
        for (; i < main_size; ++i) {
            keys.push(static_cast<K&&>(_keys[i]));
            values.push(static_cast<V&&>(_values[i]));
        }
        for (; j < pending_size; ++j) {
            keys.push(static_cast<K&&>(_pending_keys[j]));
            values.push(static_cast<V&&>(_pending_values[j]));
        }
        _keys = static_cast<array<K>&&>(keys);
        _values = static_cast<array<V>&&>(values);
        _pending_keys = array<K>(_pending_keys.allocator());
        _pending_values = array<V>(_pending_values.allocator());
    }
};

}  // namespace rgl::standard
