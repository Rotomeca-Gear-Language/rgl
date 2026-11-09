#pragma once

#include <atomic>
#include <compare>
#include <concepts>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <string>
#include <string_view>
#include <type_traits>

#include "rgl/standard/memory.hpp"

namespace rgl::standard::primitives {

namespace detail {

// Volontairement non constexpr : l'appeler pendant une évaluation à la compilation est une
// erreur de compilation, dont le message cite ce nom. Sert à refuser une vue constexpr sur un
// texte qui ne finit pas par '\0', sans utiliser d'exception (le runtime doit compiler avec
// -fno-exceptions). Jamais appelée à l'exécution.
[[noreturn]] inline void constexpr_long_string_must_be_a_complete_literal() noexcept { std::abort(); }

// Égalité de deux suites de caractères.
constexpr bool equal(const char* left, std::size_t left_size, const char* right, std::size_t right_size) noexcept {
    if (left_size != right_size) return false;
    for (std::size_t i = 0; i < left_size; ++i)
        if (left[i] != right[i]) return false;
    return true;
}

// Ordre lexicographique, caractère par caractère comme octets non signés, puis par longueur.
constexpr std::strong_ordering compare(const char* left, std::size_t left_size, const char* right, std::size_t right_size) noexcept {
    const std::size_t common = left_size < right_size ? left_size : right_size;
    for (std::size_t i = 0; i < common; ++i) {
        const auto a = static_cast<unsigned char>(left[i]);
        const auto b = static_cast<unsigned char>(right[i]);
        if (a != b) return a <=> b;
    }
    return left_size <=> right_size;
}


// Un index de caractère hors limites arrête le programme, avec l'endroit du source RGL.
[[noreturn]] inline void character_out_of_range(long long index, std::size_t length, const char* where) noexcept {
    std::fprintf(stderr, "rgl : index %lld hors limites pour un texte de %zu caractère(s) (%s)\n", index, length, where);
    std::abort();
}

// Indique si index désigne un caractère d'un texte de length caractères : un index positif compte
// depuis le début, un index négatif depuis la fin (-1 est le dernier).
template <std::integral I>
constexpr bool rank_fits(I index, std::size_t length) noexcept {
    if constexpr (std::is_signed_v<I>) {
        if (index < 0) return static_cast<unsigned long long>(-(index + 1)) < length;
    }
    return static_cast<unsigned long long>(index) < length;
}

// Le rang depuis le début du caractère désigné par index, déjà vérifié par rank_fits.
template <std::integral I>
constexpr std::size_t resolve_rank(I index, std::size_t length) noexcept {
    if constexpr (std::is_signed_v<I>) {
        if (index < 0) return length - 1 - static_cast<std::size_t>(-(index + 1));
    }
    return static_cast<std::size_t>(index);
}

}  // namespace detail

class mstring;

// Chaîne immuable : le type string de RGL.
//
// Trois représentations, choisies à la construction :
// - petite (au plus 22 caractères) : stockée dans l'objet, sans allocation ;
// - sur le tas : un bloc partagé par toutes les copies, compté par un compteur atomique ;
// - vue : uniquement pour un littéral long connu à la compilation (constexpr), qui pointe
//   directement sur le texte du littéral, sans copie ni allocation.
//
// Invariant : data() est toujours terminé par '\0', quelle que soit la représentation.
class string {
   public:
    // Nombre de caractères stockés sans allocation.
    static constexpr std::size_t small_capacity = 22;

    // Chaîne vide.
    constexpr string() noexcept = default;

    // Copie du texte C terminé par '\0' ; nullptr donne une chaîne vide.
    constexpr string(const char* text) : string(text, text ? _length(text) : 0) {}

    // Copie des size premiers caractères de text. À la compilation, un texte de plus de 22
    // caractères devient une vue : text[size] doit alors valoir '\0' (littéral complet).
    constexpr string(const char* text, std::size_t size) {
        if (!text || size == 0) return;

        if (size <= small_capacity) {
            _set_small(text, size);
            return;
        }

        if (std::is_constant_evaluated()) {
            if (text[size] != '\0') detail::constexpr_long_string_must_be_a_complete_literal();
            ptr_ = text;
            size_ = size;
            kind_ = Kind::View;
            return;
        }

        _init_heap(text, size, default_allocator());
    }

    // Copie des size premiers caractères de text, avec un allocateur choisi (exécution seulement).
    string(const char* text, std::size_t size, const AbiAllocator& allocator);

    // Frontière C++ ergonomique : copie un std::string sans supposer qu'il ne contient pas de zéro.
    explicit string(const std::string& text) : string(text.data(), text.size()) {}

    static string from_cstr(const char* text) { return string(text); }
    static string from_std_string(const std::string& text) { return string(text.data(), text.size()); }

    // Copie : partage le bloc du tas (compteur incrémenté), recopie une petite chaîne.
    constexpr string(const string& other) noexcept { _copy_from(other); }

    // Déplacement : reprend le bloc de other, qui devient vide.
    constexpr string(string&& other) noexcept { _steal(other); }

    // Les affectations sont dans l'en-tête : pour une petite chaîne, le compilateur voit qu'il n'y
    // a rien à libérer et réduit l'affectation à une copie de champs, voire à rien.

    // Copie : copie d'abord, libère ensuite, ce qui reste correct si other partage le bloc de *this. Le texte d'une arène n'est jamais partagé
    // par une affectation : *this peut avoir été créé hors de l'arène et lui survivre.
    constexpr string& operator=(const string& other) noexcept {
        if (this == &other) return *this;
        if (other.kind_ == Kind::Heap && other.alloc_->scoped) {
            _assign_detached(other);
            return *this;
        }
        string copy(other);
        if (kind_ == Kind::Heap) _release();
        _steal(copy);
        return *this;
    }

    // Déplacement : libère l'ancien bloc s'il y en a un, puis reprend celui de other.
    constexpr string& operator=(string&& other) noexcept {
        if (this == &other) return *this;
        if (other.kind_ == Kind::Heap && other.alloc_->scoped) {
            _assign_detached(other);
            other._release();
            return *this;
        }
        if (kind_ == Kind::Heap) _release();
        _steal(other);
        return *this;
    }

    constexpr ~string() {
        if (kind_ == Kind::Heap) _release();
    }

    constexpr const char* data() const noexcept { return kind_ == Kind::Small ? small_ : ptr_; }
    constexpr const char* c_str() const noexcept { return data(); }
    constexpr std::size_t size() const noexcept { return size_; }
    constexpr bool empty() const noexcept { return size_ == 0; }
    constexpr char operator[](std::size_t index) const noexcept { return data()[index]; }
    constexpr const char* begin() const noexcept { return data(); }
    constexpr const char* end() const noexcept { return data() + size_; }
    constexpr std::string_view view() const noexcept { return {data(), size()}; }
    std::string to_std_string() const { return {data(), size()}; }

    // Sous-chaîne de count caractères à partir de position (copie). Une position au-delà de la
    // fin donne une chaîne vide ; count est ramené à ce qui reste.
    string substr(std::size_t position, std::size_t count = static_cast<std::size_t>(-1)) const;

    // Nombre de caractères : des points de code Unicode, un caractère pouvant prendre plusieurs octets. Pour un texte sur le tas, le compte est
    // fait une fois, à la première demande, et partagé par toutes les copies : length() est ensuite immédiat.
    std::size_t length() const noexcept;

    // Vrai si chaque caractère tient dans un octet : le rang d'un caractère est alors son rang d'octet, et s[i] ne parcourt rien.
    bool is_ascii() const noexcept { return length() == size_; }

    // Le caractère de rang rank (depuis 0), copié dans une chaîne ; chaîne vide hors limites.
    string character(std::size_t rank) const;

    template <std::integral I>
    bool has_index(I index) const noexcept {
        return detail::rank_fits(index, length());
    }

    // Le caractère index, ou l'arrêt du programme hors limites. Un index négatif compte depuis la fin.
    template <std::integral I>
    string at(I index, const char* where) const noexcept {
        const std::size_t count = length();
        if (!detail::rank_fits(index, count)) detail::character_out_of_range(static_cast<long long>(index), count, where);
        return character(detail::resolve_rank(index, count));
    }

    friend constexpr bool operator==(const string& left, const string& right) noexcept {
        return detail::equal(left.data(), left.size_, right.data(), right.size_);
    }

    friend constexpr bool operator==(const string& left, const char* right) noexcept {
        return right && detail::equal(left.data(), left.size_, right, _length(right));
    }

    friend constexpr std::strong_ordering operator<=>(const string& left, const string& right) noexcept {
        return detail::compare(left.data(), left.size_, right.data(), right.size_);
    }

    friend string operator+(const string& left, const string& right);
    friend string operator+(const string& left, const char* right);
    friend string operator+(const char* left, const string& right);

    friend class mstring;

    // Chaîne de size caractères, écrits par fill(char* zone) qui en écrit exactement size : une
    // seule allocation, aucune pour une petite chaîne. Sert aux templates, dont la taille est
    // calculée d'abord.
    template <class Fill>
    static string with_size(std::size_t size, Fill&& fill) {
        string result;
        char* target = size <= small_capacity ? result.small_ : result._init_heap_uninitialized(size, default_allocator());
        fill(target);
        target[size] = '\0';
        result.size_ = size;
        return result;
    }

   private:
    enum class Kind : std::uint8_t { Small, Heap, View };

    // En-tête d'un bloc du tas ; les caractères suivent immédiatement.
    struct Control {
        std::atomic<std::size_t> references;
        // Le nombre de caractères Unicode, calculé à la première demande de length() ; npos tant qu'il ne l'est pas. Le texte d'un bloc ne change
        // jamais : le compte vaut pour toutes les copies.
        std::atomic<std::size_t> characters{static_cast<std::size_t>(-1)};
    };

    Control* ctrl_ = nullptr;
    const char* ptr_ = nullptr;
    std::size_t size_ = 0;
    const AbiAllocator* alloc_ = nullptr;
    Kind kind_ = Kind::Small;
    char small_[small_capacity + 1] = {};

    static constexpr std::size_t _length(const char* text) noexcept {
        std::size_t size = 0;
        while (text[size] != '\0') ++size;
        return size;
    }

    constexpr void _set_small(const char* text, std::size_t size) noexcept {
        for (std::size_t i = 0; i < size; ++i) small_[i] = text[i];
        small_[size] = '\0';
        size_ = size;
        kind_ = Kind::Small;
    }

    // Une copie partage le bloc du tas de l'original, sauf pour celui d'une arène qui n'est pas celle où l'on crée (l'allocateur ambiant) : sa
    // mémoire est rendue à la remise à zéro de l'arène, et la copie peut lui survivre. Elle a alors son propre bloc, sur le tas.
    constexpr void _copy_from(const string& other) noexcept {
        if (other.kind_ == Kind::Heap && other.alloc_->scoped && other.alloc_ != &default_allocator()) {
            _deep_copy_from(other);
            return;
        }
        size_ = other.size_;
        kind_ = other.kind_;
        if (kind_ == Kind::Small) {
            for (std::size_t i = 0; i <= size_; ++i) small_[i] = other.small_[i];
            return;
        }
        ptr_ = other.ptr_;
        ctrl_ = other.ctrl_;
        alloc_ = other.alloc_;
        if (kind_ == Kind::Heap) _retain();
    }

    constexpr void _steal(string& other) noexcept {
        // Un déplacement reprend le bloc, sauf celui d'une arène qui n'est pas celle où l'on crée (voir _copy_from) : le texte passe dans un bloc du tas, et other est vidé.
        if (other.kind_ == Kind::Heap && other.alloc_->scoped && other.alloc_ != &default_allocator()) {
            _deep_copy_from(other);
            other._release();
            return;
        }
        _copy_fields(other);
        other._reset();
    }

    constexpr void _copy_fields(const string& other) noexcept {
        ctrl_ = other.ctrl_;
        ptr_ = other.ptr_;
        size_ = other.size_;
        alloc_ = other.alloc_;
        kind_ = other.kind_;
        for (std::size_t i = 0; i <= small_capacity; ++i) small_[i] = other.small_[i];
    }

    constexpr void _reset() noexcept {
        ctrl_ = nullptr;
        ptr_ = nullptr;
        size_ = 0;
        alloc_ = nullptr;
        kind_ = Kind::Small;
        small_[0] = '\0';
    }

    // Prépare un bloc du tas de size caractères (le '\0' final est déjà posé) et renvoie la zone à remplir.
    char* _init_heap_uninitialized(std::size_t size, const AbiAllocator& allocator);
    void _deep_copy_from(const string& other);
    void _assign_detached(const string& other);
    void _init_heap(const char* text, std::size_t size, const AbiAllocator& allocator);
    void _retain() const noexcept;
    void _release() noexcept;

    // Chaîne de left_size + right_size caractères : left suivi de right.
    static string _concat(const char* left, std::size_t left_size, const char* right, std::size_t right_size);
};

// Chaîne modifiable : le type mstring de RGL.
//
// Petite chaîne (au plus 22 caractères) dans l'objet, sinon tampon sur le tas qui grandit d'un
// facteur 1,5. Un mstring n'est jamais partagé : chaque copie a son propre tampon. Il garde son
// allocateur toute sa vie (une copie par affectation ne le change pas). Si la mémoire manque,
// le programme s'arrête (out_of_memory) plutôt que d'écrire au-delà du tampon.
//
// Invariant : data() est toujours terminé par '\0'.
class mstring {
   public:
    // Nombre de caractères stockés sans allocation.
    static constexpr std::size_t small_capacity = 22;

    explicit mstring(const AbiAllocator& allocator = default_allocator()) noexcept;
    mstring(const char* text, const AbiAllocator& allocator = default_allocator());
    mstring(const char* text, std::size_t size, const AbiAllocator& allocator = default_allocator());
    explicit mstring(const string& text, const AbiAllocator& allocator = default_allocator());

    mstring(const mstring& other);
    mstring(mstring&& other) noexcept;
    mstring& operator=(const mstring& other);
    mstring& operator=(mstring&& other) noexcept;
    ~mstring();

    const char* data() const noexcept { return is_small_ ? small_ : buffer_; }
    char* data() noexcept { return is_small_ ? small_ : buffer_; }
    const char* c_str() const noexcept { return data(); }
    std::size_t size() const noexcept { return size_; }
    std::size_t capacity() const noexcept { return is_small_ ? small_capacity : capacity_; }
    bool empty() const noexcept { return size_ == 0; }
    char operator[](std::size_t index) const noexcept { return data()[index]; }
    char& operator[](std::size_t index) noexcept { return data()[index]; }
    const AbiAllocator& allocator() const noexcept { return *alloc_; }

    void clear() noexcept;
    void reserve(std::size_t capacity);
    void shrink_to_fit() noexcept;

    void push_back(char character) { append(&character, 1); }
    void append(const char* text);
    // text peut pointer dans ce mstring lui-même (m.append(m.data(), 3)).
    void append(const char* text, std::size_t count);
    void append(const string& text) { append(text.data(), text.size()); }
    void append(const mstring& text) { append(text.data(), text.size()); }

    // Insère count caractères de text à position (ramenée à la fin si elle la dépasse).
    void insert(std::size_t position, const char* text, std::size_t count);
    // Supprime au plus count caractères à partir de position.
    void erase(std::size_t position, std::size_t count) noexcept;
    // Tronque, ou complète avec fill, pour obtenir size caractères.
    void resize(std::size_t size, char fill = '\0');

    // Copie immuable du contenu.
    string to_immutable() const;

    mstring& operator+=(const char* text) { append(text); return *this; }
    mstring& operator+=(char character) { push_back(character); return *this; }
    mstring& operator+=(const string& text) { append(text); return *this; }
    mstring& operator+=(const mstring& text) { append(text); return *this; }

    friend mstring operator+(mstring left, const char* right) { left.append(right); return left; }
    friend mstring operator+(mstring left, const string& right) { left.append(right); return left; }
    friend mstring operator+(mstring left, const mstring& right) { left.append(right); return left; }

    friend bool operator==(const mstring& left, const mstring& right) noexcept {
        return detail::equal(left.data(), left.size_, right.data(), right.size_);
    }

    friend std::strong_ordering operator<=>(const mstring& left, const mstring& right) noexcept {
        return detail::compare(left.data(), left.size_, right.data(), right.size_);
    }

   private:
    char* buffer_ = nullptr;
    std::size_t size_ = 0;
    std::size_t capacity_ = 0;
    const AbiAllocator* alloc_;
    bool is_small_ = true;
    char small_[small_capacity + 1] = {};

    // Garantit la place pour needed caractères (plus le '\0'), en agrandissant d'un facteur 1,5.
    void _ensure_capacity(std::size_t needed);
    // Vrai si text pointe dans le contenu de ce mstring.
    bool _contains(const char* text) const noexcept;
    void _release_buffer() noexcept;
};

}  // namespace rgl::standard::primitives

namespace rgl::standard {

using primitives::mstring;
using primitives::string;

}  // namespace rgl::standard
